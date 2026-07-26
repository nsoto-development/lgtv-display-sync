using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LgtvDisplaySync.App;

// Receives Windows monitor power-state changes via GUID_CONSOLE_DISPLAY_STATE on a
// message-only window in the current (interactive) session. Raises DisplayOn(true/false).
// Data: 0 = off, 1 = on, 2 = dimmed. We treat dimmed as "on" (not off), matching how
// the display is still powered.
public sealed class MonitorPowerWatcher : NativeWindow, IDisposable
{
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;
    private const int HWND_MESSAGE = -3;
    private static Guid _guidConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    private IntPtr _notify;

    // true = display on (or dimmed), false = display off
    public event Action<bool>? DisplayStateChanged;

    [StructLayout(LayoutKind.Sequential)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid PowerSettingGuid, int Flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterPowerSettingNotification(IntPtr Handle);

    public MonitorPowerWatcher()
    {
        CreateHandle(new CreateParams { Caption = "LgtvDisplaySyncMsgWnd", Parent = HWND_MESSAGE });
        _notify = RegisterPowerSettingNotification(Handle, ref _guidConsoleDisplayState, DEVICE_NOTIFY_WINDOW_HANDLE);
        if (_notify == IntPtr.Zero)
            throw new InvalidOperationException($"RegisterPowerSettingNotification failed (win32 {Marshal.GetLastWin32Error()})");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_POWERBROADCAST && (int)m.WParam == PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
        {
            var ps = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(m.LParam);
            if (ps.PowerSetting == _guidConsoleDisplayState)
                DisplayStateChanged?.Invoke(ps.Data != 0); // 0=off -> false; 1/2 -> true
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_notify != IntPtr.Zero) { UnregisterPowerSettingNotification(_notify); _notify = IntPtr.Zero; }
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}
