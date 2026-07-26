using System.Runtime.InteropServices;

namespace LgtvDisplaySync.App;

// Receives Windows monitor power-state changes via GUID_CONSOLE_DISPLAY_STATE on a
// message-only window in the current (interactive) session. Raises DisplayOn(true/false).
// Data: 0 = off, 1 = on, 2 = dimmed. We treat dimmed as "on" (not off), matching how
// the display is still powered.
public sealed class MonitorPowerWatcher : IDisposable
{
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private static Guid _guidConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    private readonly WndProc _wndProc; // keep rooted so GC does not collect the callback
    private readonly string _className;
    private IntPtr _hwnd;
    private IntPtr _notify;
    private bool _classRegistered;

    // true = display on (or dimmed), false = display off
    public event Action<bool>? DisplayStateChanged;

    public IntPtr Handle => _hwnd;

    [StructLayout(LayoutKind.Sequential)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
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

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid PowerSettingGuid, int Flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterPowerSettingNotification(IntPtr Handle);

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

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    public MonitorPowerWatcher()
    {
        _wndProc = WindowProc;
        _className = "LgtvDisplaySyncMsgWnd." + Guid.NewGuid().ToString("N");
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
            0, _className, "LgtvDisplaySyncMsgWnd", 0,
            0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed (win32 {Marshal.GetLastWin32Error()})");

        _notify = RegisterPowerSettingNotification(_hwnd, ref _guidConsoleDisplayState, DEVICE_NOTIFY_WINDOW_HANDLE);
        if (_notify == IntPtr.Zero)
            throw new InvalidOperationException($"RegisterPowerSettingNotification failed (win32 {Marshal.GetLastWin32Error()})");
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_POWERBROADCAST && (int)wParam == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
        {
            var ps = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
            if (ps.PowerSetting == _guidConsoleDisplayState)
                DisplayStateChanged?.Invoke(ps.Data != 0); // 0=off -> false; 1/2 -> true
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_notify != IntPtr.Zero) { UnregisterPowerSettingNotification(_notify); _notify = IntPtr.Zero; }
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        if (_classRegistered)
        {
            UnregisterClass(_className, GetModuleHandle(null));
            _classRegistered = false;
        }
    }
}
