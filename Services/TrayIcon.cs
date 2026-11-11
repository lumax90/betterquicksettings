using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using BetterQuickSettings.Interop;

namespace BetterQuickSettings.Services;

/// <summary>A native tray icon, using the existing WPF HWND rather than a second UI framework.</summary>
public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8064;
    private static readonly uint TaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    private readonly IntPtr window, icon;
    private readonly string title;
    private readonly HwndSource source;
    private bool version4;
    public event Action? OpenRequested;
    public event Action? ExitRequested;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct IconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    public TrayIcon(IntPtr window, string title)
    {
        this.window = window; this.title = title;
        icon = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"), 1, 32, 32, 0x10);
        if (icon == IntPtr.Zero) throw new InvalidOperationException("The tray icon could not be loaded.");
        source = HwndSource.FromHwnd(window) ?? throw new InvalidOperationException("The panel window is unavailable.");
        source.AddHook(WindowMessage);
        Add();
    }

    private IconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<IconData>(), Window = window, Id = 1,
        Icon = icon, Callback = CallbackMessage, Tip = title, Info = "", InfoTitle = ""
    };

    private void Add()
    {
        var data = Data();
        data.Flags = 1 | 2 | 4 | 0x80;
        if (!ShellNotifyIcon(0, ref data)) return;
        data.TimeoutOrVersion = 4;
        version4 = ShellNotifyIcon(4, ref data);
    }

    public void ShowBalloon(string caption, string text)
    {
        var data = Data();
        data.Flags = 0x10;
        data.InfoTitle = caption;
        data.Info = text;
        data.InfoFlags = 1;
        ShellNotifyIcon(1, ref data);
    }

    private IntPtr WindowMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == TaskbarCreated) Add();
        if (message != CallbackMessage) return IntPtr.Zero;
        var action = version4 ? (int)(lParam.ToInt64() & 0xFFFF) : lParam.ToInt32();
        if ((version4 && action is 0x400 or 0x401) || (!version4 && action == 0x202)) OpenRequested?.Invoke();
        else if ((version4 && action == 0x007B) || (!version4 && action == 0x0205)) ShowMenu();
        handled = true;
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, 0, 1, "Open control center");
            AppendMenu(menu, 0, 2, "Exit");
            Native.GetCursorPos(out var point);
            Native.SetForegroundWindow(window);
            var selected = TrackPopupMenuEx(menu, 0x100 | 2, point.X, point.Y, window, IntPtr.Zero);
            Native.PostMessage(window, 0, IntPtr.Zero, IntPtr.Zero);
            if (selected == 1) OpenRequested?.Invoke();
            else if (selected == 2) ExitRequested?.Invoke();
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (!source.IsDisposed) source.RemoveHook(WindowMessage);
        var data = Data();
        ShellNotifyIcon(2, ref data);
        DestroyIcon(icon);
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint message, ref IconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
}
