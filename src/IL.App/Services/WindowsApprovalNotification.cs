using System.Runtime.InteropServices;
using Avalonia.Controls;
using IL.Core.Infrastructure;

namespace IL.App.Services;

internal sealed class WindowsApprovalNotification : IDisposable
{
    private const uint CallbackMessage = 0x8421, IconId = 0x494C;
    private readonly Window _window;
    private nint _handle;
    private Action? _clicked;
    private bool _added;
    public WindowsApprovalNotification(Window window)
    {
        _window = window;
        Win32Properties.AddWndProcHookCallback(window, OnWindowMessage);
    }
    public void Show(string agentName, Action clicked)
    {
        Close();
        _handle = _window.TryGetPlatformHandle()?.Handle ?? 0;
        if (_handle == 0) return;
        _clicked = clicked;
        var data = Data();
        data.Flags = 0x01 | 0x02 | 0x04 | 0x08; // message, icon, tooltip, state
        data.Callback = CallbackMessage;
        data.Icon = SendMessage(_handle, 0x007F, 2, 0); // WM_GETICON / ICON_SMALL2
        if (data.Icon == 0) data.Icon = LoadIcon(0, (nint)32512);
        data.Tip = "Intensive Listening";
        // A notification-only icon keeps the existing application tray icon as the visible entry.
        data.State = data.StateMask = 1;
        _added = ShellNotifyIcon(0, ref data);
        if (!_added) { AppLog.Warning("无法注册智能体审批通知"); return; }
        data.Version = 4;
        ShellNotifyIcon(4, ref data);
        data.Flags = 0x10;
        data.InfoTitle = "智能体请求接管";
        data.Info = $"{agentName} 请求接管 Intensive Listening，点击打开应用并审批";
        data.InfoFlags = 1;
        if (!ShellNotifyIcon(1, ref data)) AppLog.Warning("无法显示智能体审批通知");
    }
    private nint OnWindowMessage(nint window, uint message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != CallbackMessage) return 0;
        var notification = unchecked((uint)lParam.ToInt64());
        if ((notification >> 16) != IconId) return 0;
        handled = true;
        if ((notification & 0xFFFF) == 0x0405) // NIN_BALLOONUSERCLICK
        {
            var clicked = _clicked;
            Close();
            clicked?.Invoke();
        }
        return 0;
    }
    public void Close()
    {
        _clicked = null;
        if (!_added) return;
        var data = Data();
        ShellNotifyIcon(2, ref data);
        _added = false;
    }
    private NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _handle, Id = IconId,
        Tip = "", Info = "", InfoTitle = ""
    };
    public void Dispose()
    {
        Close();
        Win32Properties.RemoveWndProcHookCallback(_window, OnWindowMessage);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string? Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern nint LoadIcon(nint instance, nint name);
}
