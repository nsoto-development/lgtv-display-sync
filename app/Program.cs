using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace LgtvDisplaySync.App;

internal static class Program
{
    private static Config _cfg = new();
    private static SsapClient? _tv;
    private static SsapClient Tv => _tv ?? throw new InvalidOperationException("SSAP client not initialized");
    private static CancellationTokenSource? _current;
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static bool _watchOnly;

    [STAThread]
    private static int Main(string[] args)
    {
        _cfg = Config.Load();
        _watchOnly = Array.Exists(args, a => a == "--watch-only");
        string? keyOverride = null;
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--keyfile" && i + 1 < args.Length) keyOverride = args[i + 1];

        var asService = WindowsServiceHelpers.IsWindowsService();
        var tray = !asService && Array.Exists(args, a => a == "--tray");
        Log($"lgtv-display-sync starting — TV {_cfg.Ip}:{_cfg.Port} mac={_cfg.Mac} off={_cfg.OffAction} session={GetSessionInfo()} host={(asService ? "service" : tray ? "tray" : "console")}{(_watchOnly ? " mode=watch-only" : "")}");

        // User-session companion for the installed service (no watcher / SSAP in this process).
        if (tray)
        {
            FreeConsole();
            return TrayCompanion.Run();
        }

        if (_watchOnly)
            return RunHostedOrConsole(asService);

        _tv = new SsapClient(_cfg.Ip, _cfg.Port, new KeyStore(_cfg.Ip, keyOverride ?? _cfg.KeyFile));

        // First-run pairing: connect once (long wait for the on-screen prompt) and save the key.
        // CLI one-shots always bypass the service host.
        if (Array.Exists(args, a => a == "--pair"))
        {
            using var pcts = new CancellationTokenSource(90_000);
            var paired = _tv.ConnectAsync(perAttemptTimeoutMs: 6000, Log, pcts.Token).GetAwaiter().GetResult();
            Log($"--pair result: {(paired ? "OK (key stored)" : "FAILED")}");
            _tv.Dispose();
            return paired ? 0 : 1;
        }

        // Test modes: run one action and exit (no display cycle needed).
        // --test on|off|poweroff|poweron
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--test" && i + 1 < args.Length)
            {
                var what = args[i + 1].ToLowerInvariant();
                using var cts = new CancellationTokenSource(90_000);
                var ok = what switch
                {
                    "on" => WakeAndScreenOnAsync(cts.Token).GetAwaiter().GetResult(),
                    "off" => ScreenOffAsync(cts.Token).GetAwaiter().GetResult(),
                    "poweroff" => PowerOffAsync(cts.Token).GetAwaiter().GetResult(),
                    "poweron" => PowerOnAsync(cts.Token).GetAwaiter().GetResult(),
                    _ => false
                };
                Log($"--test {what} result: {(ok ? "OK" : "FAILED")}");
                _tv.Dispose();
                return ok ? 0 : 1;
            }
        }

        return RunHostedOrConsole(asService);
    }

    private static int RunHostedOrConsole(bool asService) =>
        asService ? RunAsWindowsService() : RunAsConsole();

    private static int RunAsConsole()
    {
        RunWatcherUntilQuit(consoleCancel: true, CancellationToken.None);
        return 0;
    }

    private static int RunAsWindowsService()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWindowsService(o => o.ServiceName = "lgtv-display-sync");
        builder.Services.AddHostedService<WatcherHostedService>();
        builder.Build().Run();
        return 0;
    }

    /// <summary>
    /// Shared watcher + Win32 message pump. Used by console and the Windows service worker thread.
    /// </summary>
    internal static void RunWatcherUntilQuit(bool consoleCancel, CancellationToken stoppingToken)
    {
        MonitorPowerWatcher? watcher = null;
        CancellationTokenRegistration stopReg = default;
        try
        {
            watcher = new MonitorPowerWatcher();
            watcher.DisplayStateChanged += OnDisplayStateChanged;
            Log(_watchOnly
                ? "watch-only: registered for monitor power events (no SSAP/WoL)."
                    + (consoleCancel ? " (Ctrl+C to quit)" : "")
                : "registered for monitor power events; waiting."
                    + (consoleCancel ? " (Ctrl+C to quit)" : ""));
        }
        catch (Exception ex)
        {
            Log($"FAILED to register monitor watcher: {ex.GetType().Name}: {ex.Message}");
        }

        if (consoleCancel)
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; PostQuitMessage(0); };

        if (stoppingToken.CanBeCanceled)
            stopReg = stoppingToken.Register(() => PostQuitMessage(0));

        try
        {
            RunMessageLoop();
        }
        finally
        {
            stopReg.Dispose();
            _current?.Cancel();
            watcher?.Dispose();
            _tv?.Dispose();
            Log("stopped.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int nExitCode);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    private static void RunMessageLoop()
    {
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private static string GetSessionInfo()
    {
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            return $"{Environment.UserName}/session{p.SessionId}";
        }
        catch { return "?"; }
    }

    private static void OnDisplayStateChanged(bool displayOn)
    {
        Log($"display -> {(displayOn ? "ON" : "OFF")}");
        if (_watchOnly) return;

        _current?.Cancel();
        var cts = new CancellationTokenSource();
        _current = cts;
        _ = Task.Run(() => HandleAsync(displayOn, cts.Token));
    }

    private static async Task HandleAsync(bool displayOn, CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct); // let any in-flight action unwind first
        }
        catch (OperationCanceledException) { return; }
        try
        {
            if (ct.IsCancellationRequested) return;
            if (displayOn)
                await WakeAndScreenOnAsync(ct); // WoL + connect + screen-on covers both off modes
            else if (_cfg.OffAction.Equals("power", StringComparison.OrdinalIgnoreCase))
                await PowerOffAsync(ct);
            else
                await ScreenOffAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"handler error: {ex.GetType().Name}: {ex.Message}"); }
        finally { _gate.Release(); }
    }

    // Display turned off -> tell the TV to turn its screen off. Best-effort, short budget.
    private static async Task<bool> ScreenOffAsync(CancellationToken ct)
    {
        if (!await Tv.EnsureConnectedAsync(perAttemptMs: 2500, totalBudgetMs: 8000, gapMs: 1000, Log, ct))
        {
            Log("screen-off: could not connect");
            return false;
        }
        var ok = await Tv.SendScreenAsync(on: false, ct);
        Log($"screen-off: {(ok ? "sent" : "failed")}");
        return ok;
    }

    // Display turned on -> WoL (in case the TV slept), then keep trying to connect and
    // send screen-on, riding through the intermittent TLS-stall waves, until it sticks
    // or the display turns off again (cancellation).
    private static async Task<bool> WakeAndScreenOnAsync(CancellationToken ct)
    {
        Wol.Send(_cfg.Mac, _cfg.Ip, Log);

        var deadline = Environment.TickCount64 + 60_000;
        while (!ct.IsCancellationRequested && Environment.TickCount64 < deadline)
        {
            if (await Tv.EnsureConnectedAsync(perAttemptMs: 2500, totalBudgetMs: 20_000, gapMs: 1500, Log, ct)
                && await Tv.SendScreenAsync(on: true, ct))
            {
                Log("screen-on: sent OK");
                return true;
            }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
        }
        Log(ct.IsCancellationRequested ? "screen-on: cancelled (display off again)" : "screen-on: gave up after 60s");
        return false;
    }

    // Display off -> put the TV into standby (real power off). Wake is via WoL only.
    private static async Task<bool> PowerOffAsync(CancellationToken ct)
    {
        if (!await Tv.EnsureConnectedAsync(perAttemptMs: 2500, totalBudgetMs: 8000, gapMs: 1000, Log, ct))
        {
            Log("power-off: could not connect");
            return false;
        }
        var ok = await Tv.SendPowerOffAsync(ct);
        Log($"power-off: {(ok ? "sent" : "failed")}");
        return ok;
    }

    // Wake the TV from standby (WoL) and wait, patiently, for it to boot and accept SSAP.
    private static async Task<bool> PowerOnAsync(CancellationToken ct)
    {
        Wol.Send(_cfg.Mac, _cfg.Ip, Log);
        Log("power-on: WoL sent; waiting for TV to boot and accept a control connection...");
        var ok = await Tv.EnsureConnectedAsync(perAttemptMs: 2500, totalBudgetMs: 60_000, gapMs: 2000, Log, ct);
        Log($"power-on: {(ok ? "TV connected" : "no connection within 60s")}");
        return ok;
    }

    private static readonly object _logLock = new();
    private static readonly string _logPath = InitLogPath();

    private static string InitLogPath()
    {
        try { AppPaths.EnsureLogDir(); } catch { /* best-effort */ }
        return AppPaths.LogFile;
    }

    private static void Log(string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {msg}";
        try { Console.WriteLine(line); Console.Out.Flush(); } catch { }
        lock (_logLock)
        {
            try
            {
                AppPaths.EnsureLogDir();
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
            catch { /* non-fatal */ }
        }
    }
}

// Effective config: config.json next to the exe overrides defaults.
internal sealed record Config
{
    // Placeholder defaults; set your real values in config.json (git-ignored).
    public string Ip { get; init; } = "192.168.1.100";
    public int Port { get; init; } = 3001;
    public string Mac { get; init; } = "AA:BB:CC:DD:EE:FF";
    public string? KeyFile { get; init; } // null -> ProgramData (or local override / ColorControl migrate)
    public string OffAction { get; init; } = "power"; // "power" (standby) or "screen" (panel off)

    public static Config Load()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Config();
        }
        catch { /* fall back to defaults */ }
        return new Config();
    }
}
