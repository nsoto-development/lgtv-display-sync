using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;

namespace LgtvDisplaySync.App;

/// <summary>
/// User-session tray companion for the installed Windows service. Does not run the display watcher.
/// </summary>
internal static class TrayCompanion
{
    internal const string ServiceName = "lgtv-display-sync";
    internal const string ElevatedCtlArg = "--elevated-service-ctl";
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED — UAC declined

    private const uint WM_APP = 0x8000;
    private const uint WM_TRAY = WM_APP + 1;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_GRAYED = 0x00000001;
    private const uint MF_DISABLED = 0x00000002;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint LR_DEFAULTSIZE = 0x0040;
    private const int ID_STATUS = 1000;
    private const int ID_START = 1001;
    private const int ID_STOP = 1002;
    private const int ID_OPEN_LOGS = 1003;
    private const int ID_EXIT = 1004;
    private const uint PollIntervalMs = 2000;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private static WndProc? _wndProc; // keep rooted for GC
    private static IntPtr _hwnd;
    private static IntPtr _hIcon;
    private static bool _iconAdded;
    private static string _className = "";
    private static bool _classRegistered;
    private static ServiceUiState _state = ServiceUiState.NotInstalled;

    private enum ServiceUiState
    {
        NotInstalled,
        Stopped,
        Running,
        Other,
    }

    public static int Run()
    {
        _wndProc = WindowProc;
        _className = "LgtvDisplaySyncTray." + Guid.NewGuid().ToString("N");
        var hInstance = GetModuleHandle(null);

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = _className,
        };
        if (RegisterClassEx(ref wc) == 0)
            throw new InvalidOperationException($"RegisterClassEx failed (win32 {Marshal.GetLastWin32Error()})");
        _classRegistered = true;

        _hwnd = CreateWindowEx(
            0, _className, "LgtvDisplaySyncTray", 0,
            0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed (win32 {Marshal.GetLastWin32Error()})");

        _hIcon = LoadTrayIcon();
        _state = QueryServiceState();
        AddOrUpdateNotifyIcon(NIM_ADD);
        SetTimer(_hwnd, (UIntPtr)1, PollIntervalMs, IntPtr.Zero);

        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            KillTimer(_hwnd, (UIntPtr)1);
            if (_iconAdded)
            {
                var nid = MakeNotifyIconData(tip: "");
                Shell_NotifyIcon(NIM_DELETE, ref nid);
                _iconAdded = false;
            }
            if (_hIcon != IntPtr.Zero)
            {
                DestroyIcon(_hIcon);
                _hIcon = IntPtr.Zero;
            }
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
            if (_classRegistered)
            {
                UnregisterClass(_className, GetModuleHandle(null));
                _classRegistered = false;
            }
        }

        return 0;
    }

    private static IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAY)
        {
            var mouse = (uint)lParam.ToInt64() & 0xFFFF;
            if (mouse is WM_RBUTTONUP or WM_LBUTTONUP)
                ShowContextMenu(hWnd);
            return IntPtr.Zero;
        }

        if (msg == WM_TIMER && wParam == (IntPtr)1)
        {
            RefreshStatus();
            return IntPtr.Zero;
        }

        if (msg == WM_COMMAND)
        {
            switch ((int)wParam & 0xFFFF)
            {
                case ID_START: TryStartService(); break;
                case ID_STOP: TryStopService(); break;
                case ID_OPEN_LOGS: OpenLogFolder(); break;
                case ID_EXIT: PostQuitMessage(0); break;
            }
            return IntPtr.Zero;
        }

        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static void RefreshStatus()
    {
        var next = QueryServiceState();
        if (next == _state) return;
        _state = next;
        AddOrUpdateNotifyIcon(NIM_MODIFY);
    }

    private static void ShowContextMenu(IntPtr hWnd)
    {
        RefreshStatus();
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        var statusText = "Service: " + StateLabel(_state);
        AppendMenu(menu, MF_STRING | MF_GRAYED | MF_DISABLED, (UIntPtr)ID_STATUS, statusText);
        AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);

        var startFlags = MF_STRING;
        var stopFlags = MF_STRING;
        if (_state != ServiceUiState.Stopped)
            startFlags |= MF_GRAYED | MF_DISABLED;
        if (_state != ServiceUiState.Running)
            stopFlags |= MF_GRAYED | MF_DISABLED;

        AppendMenu(menu, startFlags, (UIntPtr)ID_START, "Start service");
        AppendMenu(menu, stopFlags, (UIntPtr)ID_STOP, "Stop service");
        AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenu(menu, MF_STRING, (UIntPtr)ID_OPEN_LOGS, "Open log folder");
        AppendMenu(menu, MF_STRING, (UIntPtr)ID_EXIT, "Exit");

        GetCursorPos(out var pt);
        SetForegroundWindow(hWnd);
        var cmd = (int)TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, 0, hWnd, IntPtr.Zero);
        DestroyMenu(menu);
        // Required so the menu dismisses correctly on the next click.
        PostMessage(hWnd, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);

        if (cmd != 0)
            WindowProc(hWnd, WM_COMMAND, (IntPtr)cmd, IntPtr.Zero);
    }

    private static void TryStartService() => TryControlService("start");

    private static void TryStopService() => TryControlService("stop");

    /// <summary>
    /// One-shot entry for the UAC-elevated child launched by the tray. Tray itself stays non-elevated.
    /// </summary>
    internal static int RunElevatedControl(string action)
    {
        try
        {
            ControlServiceInProcess(NormalizeAction(action));
            return 0;
        }
        catch (Exception ex)
        {
            // Child has no UI; parent surfaces failure via exit code. Log to stderr if attached.
            Console.Error.WriteLine($"elevated-service-ctl {action} failed: {ex.Message}");
            return 1;
        }
    }

    private static void TryControlService(string action)
    {
        try
        {
            if (IsElevated())
                ControlServiceInProcess(action);
            else if (!TryElevateServiceControl(action))
                return; // UAC cancelled — leave status as-is
        }
        catch (Exception ex)
        {
            MessageBox(IntPtr.Zero,
                $"Could not {action} the service.\n\n" + ex.Message,
                "lgtv-display-sync", 0x00000010 /* MB_ICONERROR */);
        }
        RefreshStatus();
        AddOrUpdateNotifyIcon(NIM_MODIFY);
    }

    private static void ControlServiceInProcess(string action)
    {
        using var sc = new ServiceController(ServiceName);
        if (action == "start")
        {
            if (sc.Status == ServiceControllerStatus.Running) return;
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        }
        else
        {
            if (sc.Status == ServiceControllerStatus.Stopped) return;
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>
    /// Returns false if the user cancelled UAC; true if the elevated child finished (success or failure).
    /// </summary>
    private static bool TryElevateServiceControl(string action)
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not resolve process path for elevation.");

        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"{ElevatedCtlArg} {action}",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p is null)
                throw new InvalidOperationException("Failed to start elevated helper.");

            if (!p.WaitForExit(60_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                throw new System.TimeoutException("Elevated start/stop did not finish within 60 seconds.");
            }

            if (p.ExitCode != 0)
            {
                MessageBox(IntPtr.Zero,
                    $"Could not {action} the service (elevated helper exited {p.ExitCode}).",
                    "lgtv-display-sync", 0x00000010 /* MB_ICONERROR */);
            }
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }

    private static string NormalizeAction(string action) =>
        action.Equals("start", StringComparison.OrdinalIgnoreCase) ? "start"
        : action.Equals("stop", StringComparison.OrdinalIgnoreCase) ? "stop"
        : throw new ArgumentException($"Unknown service control action '{action}' (expected start|stop).");

    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void OpenLogFolder()
    {
        try
        {
            var dir = AppPaths.EnsureLogDir();
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox(IntPtr.Zero, "Could not open log folder.\n\n" + ex.Message,
                "lgtv-display-sync", 0x00000010);
        }
    }

    private static ServiceUiState QueryServiceState()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status switch
            {
                ServiceControllerStatus.Running => ServiceUiState.Running,
                ServiceControllerStatus.Stopped => ServiceUiState.Stopped,
                ServiceControllerStatus.StartPending => ServiceUiState.Running,
                ServiceControllerStatus.StopPending => ServiceUiState.Stopped,
                _ => ServiceUiState.Other,
            };
        }
        catch (InvalidOperationException)
        {
            return ServiceUiState.NotInstalled;
        }
        catch
        {
            return ServiceUiState.NotInstalled;
        }
    }

    private static string StateLabel(ServiceUiState state) => state switch
    {
        ServiceUiState.Running => "Running",
        ServiceUiState.Stopped => "Stopped",
        ServiceUiState.NotInstalled => "Not installed",
        _ => "Busy…",
    };

    private static void AddOrUpdateNotifyIcon(uint msg)
    {
        var tip = "LG TV Display Sync — " + StateLabel(_state);
        var nid = MakeNotifyIconData(tip);
        if (!Shell_NotifyIcon(msg, ref nid) && msg == NIM_ADD)
            throw new InvalidOperationException($"Shell_NotifyIcon ADD failed (win32 {Marshal.GetLastWin32Error()})");
        if (msg == NIM_ADD)
            _iconAdded = true;
    }

    private static NOTIFYICONDATA MakeNotifyIconData(string tip)
    {
        var nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY,
            hIcon = _hIcon,
        };
        nid.szTip = tip.Length > 127 ? tip[..127] : tip;
        return nid;
    }

    private static IntPtr LoadTrayIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appicon.ico");
        if (File.Exists(path))
        {
            var icon = LoadImage(IntPtr.Zero, path, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
            if (icon != IntPtr.Zero) return icon;
        }
        return LoadIcon(IntPtr.Zero, (IntPtr)32512 /* IDI_APPLICATION */);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
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

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hWnd, UIntPtr uIDEvent);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
