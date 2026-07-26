using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LgtvDisplaySync.Probe;

// Instrumented LG webOS SSAP connect probe.
// Purpose: connect to the TV's control websocket in EXPLICIT phases so we can see
// exactly where a stall/failure happens (TCP vs TLS vs WS-upgrade vs SSAP register),
// with the option to bind the outgoing socket to a specific local NIC. Then toggle
// the TV screen off/on on a keypress. Reads the existing paired client-key from
// ColorControl's data path so the TV does not re-prompt.
internal static class Program
{
    // LG webOS "legacy" registration manifest. CLIENTKEYGOESHERE is replaced with the
    // paired client-key. Sent as the first SSAP message after the WS upgrade.
    private const string RegisterTemplate = """
    {
       "type":"register",
       "id":"register_0",
       "payload":{
          "forcePairing":false,
          "pairingType":"PROMPT",
          "client-key":"CLIENTKEYGOESHERE",
          "manifest":{
             "manifestVersion":1,
             "appVersion":"1.1",
             "signed":{
                "created":"20140509",
                "appId":"com.lge.test",
                "vendorId":"com.lge",
                "localizedAppNames":{ "":"LG Remote App" },
                "localizedVendorNames":{ "":"LG Electronics" },
                "permissions":[
                   "TEST_SECURE","CONTROL_INPUT_TEXT","CONTROL_MOUSE_AND_KEYBOARD",
                   "READ_INSTALLED_APPS","READ_LGE_SDX","READ_NOTIFICATIONS","SEARCH",
                   "WRITE_SETTINGS","WRITE_NOTIFICATION_ALERT","CONTROL_POWER",
                   "READ_CURRENT_CHANNEL","READ_RUNNING_APPS","READ_UPDATE_INFO",
                   "UPDATE_FROM_REMOTE_APP","READ_LGE_TV_INPUT_EVENTS","READ_TV_CURRENT_TIME"
                ],
                "serial":"2f930e2d2cfe083771f68e4fe7bb07"
             },
             "permissions":[
                "LAUNCH","LAUNCH_WEBAPP","APP_TO_APP","CLOSE","TEST_OPEN","TEST_PROTECTED",
                "CONTROL_AUDIO","CONTROL_DISPLAY","CONTROL_INPUT_JOYSTICK",
                "CONTROL_INPUT_MEDIA_RECORDING","CONTROL_INPUT_MEDIA_PLAYBACK",
                "CONTROL_INPUT_TV","CONTROL_POWER","CONTROL_TV_SCREEN","READ_APP_STATUS",
                "READ_CURRENT_CHANNEL","READ_INPUT_DEVICE_LIST","READ_NETWORK_STATE",
                "READ_RUNNING_APPS","READ_TV_CHANNEL_LIST","WRITE_NOTIFICATION_TOAST",
                "READ_POWER_STATE","READ_COUNTRY_INFO","READ_SETTINGS"
             ],
             "signatures":[
                {
                   "signatureVersion":1,
                   "signature":"eyJhbGdvcml0aG0iOiJSU0EtU0hBMjU2Iiwia2V5SWQiOiJ0ZXN0LXNpZ25pbmctY2VydCIsInNpZ25hdHVyZVZlcnNpb24iOjF9.hrVRgjCwXVvE2OOSpDZ58hR+59aFNwYDyjQgKk3auukd7pcegmE2CzPCa0bJ0ZsRAcKkCTJrWo5iDzNhMBWRyaMOv5zWSrthlf7G128qvIlpMT0YNY+n/FaOHE73uLrS/g7swl3/qH/BGFG2Hu4RlL48eb3lLKqTt2xKHdCs6Cd4RMfJPYnzgvI4BNrFUKsjkcu+WD4OO2A27Pq1n50cMchmcaXadJhGrOqH5YmHdOCj5NSHzJYrsW0HPlpuAx/ECMeIZYDh6RMqaFM2DXzdKX9NmmyqzJ3o/0lkk/N97gfVRLW5hA29yeAwaCViZNCP8iC9aO0q9fQojoa7NQnAtw=="
                }
             ]
          }
       }
    }
    """;

    private static readonly Stopwatch Wall = Stopwatch.StartNew();

    private static void Log(string msg) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} +{Wall.ElapsedMilliseconds,6}ms] {msg}");

    private static async Task<int> Main(string[] args)
    {
        var ip = "192.168.100.2";
        var port = 3001;
        string? bindIp = "192.168.100.1"; // Ethernet NIC toward the TV; --no-bind to let the OS choose
        var phaseTimeoutMs = 15000;
        string? screenOnce = null; // "off"/"on": send one screen command then exit (non-interactive)
        var connectOnly = false;   // connect+register, report, exit (no interactive loop)
        string? keyFile = null;    // explicit path to the client-key file (overrides default location)

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ip": ip = args[++i]; break;
                case "--port": port = int.Parse(args[++i]); break;
                case "--bind": bindIp = args[++i]; break;
                case "--no-bind": bindIp = null; break;
                case "--timeout": phaseTimeoutMs = int.Parse(args[++i]); break;
                case "--screen": screenOnce = args[++i]; break; // off | on
                case "--connect-only": connectOnly = true; break;
                case "--keyfile": keyFile = args[++i]; break;
                case "-h" or "--help":
                    Console.WriteLine("usage: lgprobe [--ip A.B.C.D] [--port 3001] [--bind A.B.C.D | --no-bind] [--timeout ms] [--screen off|on]");
                    return 0;
            }
        }

        Log($"LG webOS SSAP probe -> {ip}:{port}  bind={bindIp ?? "(OS default)"}  phaseTimeout={phaseTimeoutMs}ms");
        var clientKey = LoadClientKey(ip, keyFile);
        Log(clientKey is null
            ? "client-key: NONE found -> will attempt pairing (accept the PROMPT on the TV)"
            : $"client-key: loaded ({clientKey.Length} chars)");

        Socket? socket = null;
        SslStream? tls = null;
        WebSocket? ws = null;
        try
        {
            // ---- PHASE 1: TCP connect (optionally source-bound to a specific NIC) ----
            var sw = Stopwatch.StartNew();
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            if (bindIp is not null)
            {
                socket.Bind(new IPEndPoint(IPAddress.Parse(bindIp), 0));
                Log($"socket bound to {socket.LocalEndPoint}");
            }
            using (var cts = new CancellationTokenSource(phaseTimeoutMs))
                await socket.ConnectAsync(IPAddress.Parse(ip), port, cts.Token);
            Log($"PHASE 1 TCP connect .......... OK  {sw.ElapsedMilliseconds,6} ms   local={socket.LocalEndPoint}");

            var net = new NetworkStream(socket, ownsSocket: true);

            // ---- PHASE 2: TLS handshake (self-signed cert accepted) ----
            sw.Restart();
            tls = new SslStream(net, leaveInnerStreamOpen: false);
            var tlsOpts = new SslClientAuthenticationOptions
            {
                TargetHost = ip,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            };
            using (var cts = new CancellationTokenSource(phaseTimeoutMs))
                await tls.AuthenticateAsClientAsync(tlsOpts, cts.Token);
            Log($"PHASE 2 TLS handshake ........ OK  {sw.ElapsedMilliseconds,6} ms   {tls.SslProtocol}  {tls.NegotiatedCipherSuite}");

            // ---- PHASE 3: WebSocket HTTP upgrade ----
            sw.Restart();
            var wsKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var req =
                "GET / HTTP/1.1\r\n" +
                $"Host: {ip}:{port}\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Key: {wsKey}\r\n" +
                "Sec-WebSocket-Version: 13\r\n" +
                "\r\n";
            await tls.WriteAsync(Encoding.ASCII.GetBytes(req));
            await tls.FlushAsync();
            var statusLine = await ReadHttpStatusLine(tls, phaseTimeoutMs);
            Log($"PHASE 3 WS upgrade ........... {statusLine}  {sw.ElapsedMilliseconds,6} ms");
            if (!statusLine.Contains("101"))
            {
                Log("WS upgrade did not return 101 -> abort");
                return 2;
            }

            ws = WebSocket.CreateFromStream(tls, isServer: false, subProtocol: null,
                keepAliveInterval: TimeSpan.FromSeconds(30));

            // ---- PHASE 4: SSAP register (client-key handshake) ----
            sw.Restart();
            var register = RegisterTemplate.Replace("CLIENTKEYGOESHERE", clientKey ?? "");
            await SendText(ws, register);
            using (var cts = new CancellationTokenSource(phaseTimeoutMs))
            {
                while (true)
                {
                    var resp = await ReceiveText(ws, cts.Token);
                    using var doc = JsonDocument.Parse(resp);
                    var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                    Log($"  <= [{type}] {Trunc(resp, 140)}");
                    if (type == "registered") break;
                    if (type == "error") { Log("register error -> abort"); return 3; }
                    if (resp.Contains("PROMPT")) Log("  -> TV is showing a pairing PROMPT; accept it on the TV...");
                }
            }
            Log($"PHASE 4 SSAP register ........ OK  {sw.ElapsedMilliseconds,6} ms");
            Log($"===== CONNECTED & REGISTERED — total {Wall.ElapsedMilliseconds} ms =====");

            // ---- One-shot mode: send a single screen command then exit ----
            if (screenOnce is "off" or "on")
            {
                var uri1 = screenOnce == "off"
                    ? "ssap://com.webos.service.tvpower/power/turnOffScreen"
                    : "ssap://com.webos.service.tvpower/power/turnOnScreen";
                await SendText(ws, $"{{\"type\":\"request\",\"id\":\"cmd_1\",\"uri\":\"{uri1}\"}}");
                Log($"  => {uri1}");
                try
                {
                    using var oneCts = new CancellationTokenSource(3000);
                    var resp = await ReceiveText(ws, oneCts.Token);
                    Log($"  <= {Trunc(resp, 200)}");
                }
                catch (OperationCanceledException) { Log("  (no response within 3s)"); }
                return 0;
            }

            // ---- Non-interactive host (redirected input) or --connect-only: exit ----
            if (connectOnly || Console.IsInputRedirected)
            {
                Log("connect-only diagnostic complete, exiting.");
                return 0;
            }

            // ---- Interactive: toggle screen off/on ----
            using var readerCts = new CancellationTokenSource();
            var reader = BackgroundReader(ws, readerCts.Token);
            Log("keys:  [f]=screen OFF   [n]=screen ON   [q]=quit");
            var id = 0;
            while (true)
            {
                var key = Console.ReadKey(true).KeyChar;
                if (key is 'q' or 'Q') break;
                var uri = key switch
                {
                    'f' or 'F' => "ssap://com.webos.service.tvpower/power/turnOffScreen",
                    'n' or 'N' => "ssap://com.webos.service.tvpower/power/turnOnScreen",
                    _ => null
                };
                if (uri is null) continue;
                id++;
                var cmd = $"{{\"type\":\"request\",\"id\":\"cmd_{id}\",\"uri\":\"{uri}\"}}";
                await SendText(ws, cmd);
                Log($"  => {uri}");
            }
            readerCts.Cancel();
            await reader.WaitAsync(TimeSpan.FromSeconds(1)).ContinueWith(_ => { });
            return 0;
        }
        catch (OperationCanceledException)
        {
            Log($"*** STALLED: a phase exceeded the {phaseTimeoutMs} ms timeout (see last 'PHASE' line for where). ***");
            return 10;
        }
        catch (Exception ex)
        {
            Log($"*** ERROR: {ex.GetType().Name}: {ex.Message}");
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                Log($"      inner: {inner.GetType().Name}: {inner.Message}");
            return 1;
        }
        finally
        {
            try
            {
                if (ws is { State: WebSocketState.Open })
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            catch { /* ignore */ }
            ws?.Dispose();
            tls?.Dispose();
            socket?.Dispose();
        }
    }

    // Reuse ColorControl's paired key: %AppData%\Maassoft\ColorControl\{ip}_ClientKey.txt
    private static string? LoadClientKey(string ip, string? keyFile)
    {
        var path = keyFile ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Maassoft", "ColorControl", $"{ip}_ClientKey.txt");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    private static async Task<string> ReadHttpStatusLine(Stream s, int timeoutMs)
    {
        // Read the HTTP response headers one byte at a time until CRLFCRLF, so we don't
        // over-read into the first WebSocket frame. Return the first (status) line.
        using var cts = new CancellationTokenSource(timeoutMs);
        var buf = new byte[1];
        var sb = new StringBuilder();
        while (!sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            var n = await s.ReadAsync(buf.AsMemory(0, 1), cts.Token);
            if (n == 0) break;
            sb.Append((char)buf[0]);
        }
        var text = sb.ToString();
        var nl = text.IndexOf('\r');
        return (nl > 0 ? text[..nl] : text).Trim();
    }

    private static Task SendText(WebSocket ws, string text) =>
        ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<string> ReceiveText(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[8192];
        var sb = new StringBuilder();
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close)
                throw new IOException($"WebSocket closed by peer: {r.CloseStatus} {r.CloseStatusDescription}");
            sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
            if (r.EndOfMessage) return sb.ToString();
        }
    }

    private static async Task BackgroundReader(WebSocket ws, CancellationToken ct)
    {
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var msg = await ReceiveText(ws, ct);
                Log($"  <= {Trunc(msg, 140)}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"  reader stopped: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static string Trunc(string s, int max) =>
        s.Length <= max ? s : s[..max] + $"...(+{s.Length - max})";
}
