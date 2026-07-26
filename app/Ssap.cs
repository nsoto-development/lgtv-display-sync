using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LgtvDisplaySync.App;

// Minimal LG webOS SSAP client over wss://ip:3001.
// Phased connect (TCP -> TLS -> WS upgrade -> register) with a short per-attempt
// timeout, so a stalled TLS handshake (see README) is abandoned quickly and retried,
// instead of blocking for 5s+ like ColorControl's connect path. One warm connection is kept; callers
// reconnect via EnsureConnectedAsync with a retry budget.
public sealed class SsapClient(string ip, int port, KeyStore keys) : IDisposable
{
    public const string ScreenOffUri = "ssap://com.webos.service.tvpower/power/turnOffScreen";
    public const string ScreenOnUri = "ssap://com.webos.service.tvpower/power/turnOnScreen";
    public const string PowerOffUri = "ssap://system/turnOff"; // TV -> standby (wake via WoL)

    private Socket? _socket;
    private SslStream? _tls;
    private WebSocket? _ws;
    private int _cmdId;

    public bool IsConnected => _ws is { State: WebSocketState.Open };

    private static readonly string RegisterTemplate = Register.LegacyManifest;

    // One connect attempt. Transport (TCP+TLS+WS upgrade) is bounded by perAttemptTimeoutMs;
    // the SSAP register waits longer while pairing (no key yet) so the user can accept the
    // on-screen prompt. Returns true only if register succeeds; persists any new client-key.
    public async Task<bool> ConnectAsync(int perAttemptTimeoutMs, Action<string> log, CancellationToken ct)
    {
        Close();
        var key = keys.Load();
        var pairing = string.IsNullOrWhiteSpace(key);
        try
        {
            using (var tcts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                tcts.CancelAfter(perAttemptTimeoutMs);
                var t = tcts.Token;

                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await _socket.ConnectAsync(IPAddress.Parse(ip), port, t);

                var net = new NetworkStream(_socket, ownsSocket: true);
                _tls = new SslStream(net, leaveInnerStreamOpen: false);
                await _tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = ip,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, t);

                var wsKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
                var req =
                    "GET / HTTP/1.1\r\n" +
                    $"Host: {ip}:{port}\r\n" +
                    "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                    $"Sec-WebSocket-Key: {wsKey}\r\nSec-WebSocket-Version: 13\r\n\r\n";
                await _tls.WriteAsync(Encoding.ASCII.GetBytes(req), t);
                await _tls.FlushAsync(t);
                var status = await ReadStatusLine(_tls, t);
                if (!status.Contains("101"))
                {
                    log($"connect: WS upgrade returned '{status}' (not 101)");
                    Close();
                    return false;
                }
                _ws = WebSocket.CreateFromStream(_tls, isServer: false, subProtocol: null,
                    keepAliveInterval: TimeSpan.FromSeconds(30));
            }

            var registerWaitMs = pairing ? 60_000 : perAttemptTimeoutMs;
            if (pairing) log("no client-key found — pairing: ACCEPT the prompt on the TV within 60s");
            using (var rcts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                rcts.CancelAfter(registerWaitMs);
                var t = rcts.Token;
                await SendText(RegisterTemplate.Replace("CLIENTKEYGOESHERE", key ?? ""), t);
                while (true)
                {
                    var resp = await ReceiveText(_ws!, t);
                    using var doc = JsonDocument.Parse(resp);
                    var type = doc.RootElement.TryGetProperty("type", out var e) ? e.GetString() : null;
                    if (type == "registered")
                    {
                        TrySaveKey(doc, log);
                        return true;
                    }
                    if (type == "error") { log($"connect: register error: {resp}"); Close(); return false; }
                    if (resp.Contains("PROMPT")) log("connect: TV showing pairing PROMPT (accept on TV)");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Close();
            return false; // stalled/timed out — caller retries
        }
        catch (Exception ex)
        {
            log($"connect: {ex.GetType().Name}: {ex.Message}");
            Close();
            return false;
        }
    }

    private void TrySaveKey(JsonDocument doc, Action<string> log)
    {
        try
        {
            if (doc.RootElement.TryGetProperty("payload", out var pl) &&
                pl.TryGetProperty("client-key", out var ck))
            {
                var k = ck.GetString();
                if (!string.IsNullOrWhiteSpace(k)) { keys.Save(k!); log("client-key saved"); }
            }
        }
        catch { /* ignore */ }
    }

    // Ensure a live connection, retrying with a budget and gentle backoff (no storm).
    public async Task<bool> EnsureConnectedAsync(int perAttemptMs, int totalBudgetMs, int gapMs, Action<string> log, CancellationToken ct)
    {
        if (IsConnected) return true;
        var deadline = Environment.TickCount64 + totalBudgetMs;
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            attempt++;
            if (await ConnectAsync(perAttemptMs, log, ct))
            {
                if (attempt > 1) log($"connected on attempt {attempt}");
                return true;
            }
            if (Environment.TickCount64 >= deadline) return false;
            try { await Task.Delay(gapMs, ct); } catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    public Task<bool> SendScreenAsync(bool on, CancellationToken ct) =>
        SendRequestAsync(on ? ScreenOnUri : ScreenOffUri, ct);

    // Power off to standby. The TV may close the socket as it powers down, so this is
    // best-effort: success means the command was written (confirmed by observing the TV).
    public async Task<bool> SendPowerOffAsync(CancellationToken ct)
    {
        if (!IsConnected) return false;
        var id = $"cmd_{Interlocked.Increment(ref _cmdId)}";
        try
        {
            await SendText($"{{\"type\":\"request\",\"id\":\"{id}\",\"uri\":\"{PowerOffUri}\"}}", ct);
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(1500);
                await ReceiveText(_ws!, linked.Token);
            }
            catch { /* TV may close/stop responding while powering off */ }
            return true;
        }
        catch { Close(); return false; }
    }

    private async Task<bool> SendRequestAsync(string uri, CancellationToken ct)
    {
        if (!IsConnected) return false;
        var id = $"cmd_{Interlocked.Increment(ref _cmdId)}";
        try
        {
            await SendText($"{{\"type\":\"request\",\"id\":\"{id}\",\"uri\":\"{uri}\"}}", ct);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(2500);
            var resp = await ReceiveText(_ws!, linked.Token);
            return resp.Contains("\"returnValue\":true") || resp.Contains("\"id\":\"" + id + "\"");
        }
        catch
        {
            Close();
            return false;
        }
    }

    private static async Task<string> ReadStatusLine(Stream s, CancellationToken ct)
    {
        var buf = new byte[1];
        var sb = new StringBuilder();
        while (!sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            var n = await s.ReadAsync(buf.AsMemory(0, 1), ct);
            if (n == 0) break;
            sb.Append((char)buf[0]);
        }
        var text = sb.ToString();
        var nl = text.IndexOf('\r');
        return (nl > 0 ? text[..nl] : text).Trim();
    }

    private Task SendText(string text, CancellationToken ct) =>
        _ws!.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, ct);

    private static async Task<string> ReceiveText(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[8192];
        var sb = new StringBuilder();
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close)
                throw new IOException("WebSocket closed by peer");
            sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
            if (r.EndOfMessage) return sb.ToString();
        }
    }

    private void Close()
    {
        try { _ws?.Abort(); } catch { }
        _ws?.Dispose(); _ws = null;
        _tls?.Dispose(); _tls = null;
        _socket?.Dispose(); _socket = null;
    }

    public void Dispose() => Close();
}
