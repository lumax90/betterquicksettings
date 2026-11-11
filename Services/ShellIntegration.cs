using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using BetterQuickSettings.Interop;

namespace BetterQuickSettings.Services;

/// <summary>
/// Process-lifetime replacement of Win+A and identified Quick Settings hit targets.
/// Hooks use a dedicated message thread; UI Automation and other blocking work stay off that thread.
/// No Explorer injection, registry policy, shell file modification, or recorded input.
/// </summary>
public sealed class ShellIntegration : IDisposable
{
    private sealed record Target(IntPtr Tray, Rect Bounds);
    private volatile Target[] targets = Array.Empty<Target>();
    private volatile Rect[] dropCorners = Array.Empty<Rect>();
    private volatile IntPtr[] trayWindows = Array.Empty<IntPtr>();
    private readonly Dispatcher dispatcher;
    private readonly Native.WinEventProc layoutProc;
    private IntPtr layoutHook;
    private uint explorerProcess;
    private int startupRetries;
    private readonly Thread thread;
    private readonly Timer refresh;
    private readonly Native.HookProc mouseProc;
    private readonly Native.HookProc keyboardProc;
    private IntPtr mouseHook, keyboardHook;
    private uint threadId;
    private int refreshing;
    private bool mouseCaptured, keyCaptured;
    private bool leftDown, dragOffered, dragInTarget;
    private long lastDropOffer;
    private Native.Point dragStart;
    private volatile bool enabled;
    private volatile bool disposed;
    public bool KeyboardReady { get; private set; }
    public bool TaskbarReady => targets.Length > 0 && mouseHook != IntPtr.Zero;
    public event Action? ToggleRequested;
    public event Action? StatusChanged;
    public event Action<int, int, Rect?>? DragTargetEntered;
    public event Action? DragReleased;
    public event Action? ScreenshotRequested;

    public bool Enabled { get => enabled; set => enabled = value; }

    public ShellIntegration(bool enabled, Dispatcher dispatcher)
    {
        this.enabled = enabled;
        this.dispatcher = dispatcher;
        layoutProc = OnShellLayout;
        mouseProc = MouseHook;
        keyboardProc = KeyboardHook;
        thread = new Thread(Run) { IsBackground = true, Name = "BetterQuickSettings shortcut listener" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        refresh = new Timer(_ => RefreshTargets(), null, 400, Timeout.Infinite);
    }

    public void RequestRefresh()
    {
        if (disposed) return;
        startupRetries = 0;
        try { refresh.Change(300, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    private void OnShellLayout(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (disposed || window == IntPtr.Zero || eventType is 0x8005 or 0x8006 or 0x8007 or 0x8008 or 0x8009 or 0x800A) return;
        var root = Native.GetAncestor(window, 2);
        foreach (var tray in trayWindows)
            if (window == tray || root == tray) { RequestRefresh(); break; }
    }

    private void WatchExplorer(uint process)
    {
        if (disposed || explorerProcess == process) return;
        if (layoutHook != IntPtr.Zero) Native.UnhookWinEvent(layoutHook);
        layoutHook = IntPtr.Zero;
        explorerProcess = process;
        if (process != 0)
            layoutHook = Native.SetWinEventHook(0x8000, 0x800B, IntPtr.Zero, layoutProc, process, 0, 2);
    }

    private void Run()
    {
        Native.EnsureMessageQueue();
        threadId = Native.GetCurrentThreadId();
        if (disposed) return;
        try
        {
            mouseHook = Native.SetWindowsHookEx(14, mouseProc, Native.GetModuleHandle(null), 0);
            keyboardHook = Native.SetWindowsHookEx(13, keyboardProc, Native.GetModuleHandle(null), 0);
            KeyboardReady = keyboardHook != IntPtr.Zero;
            StatusChanged?.Invoke();
            Native.PumpMessages();
        }
        finally
        {
            if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
            if (keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyboardHook);
        }
    }

    private void RefreshTargets()
    {
        if (disposed || Interlocked.Exchange(ref refreshing, 1) != 0) return;
        try
        {
            var corners = new List<Rect>();
            foreach (var screen in Native.Displays())
                corners.Add(new Rect(screen.Bounds.Right - 110, screen.Bounds.Bottom - 96, 110, 96));
            dropCorners = corners.ToArray();
            var list = new List<Target>();
            // Resolve native taskbar HWNDs directly; never activate providers for other desktop apps.
            var handles = new List<IntPtr>();
            var primary = Native.FindWindow("Shell_TrayWnd", null);
            if (primary != IntPtr.Zero) handles.Add(primary);
            var secondary = IntPtr.Zero;
            while ((secondary = Native.FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                handles.Add(secondary);
            trayWindows = handles.ToArray();
            Native.GetWindowThreadProcessId(primary, out var process);
            dispatcher.BeginInvoke(new Action(() => WatchExplorer(process)));
            foreach (var handle in handles)
            {
                var tray = AutomationElement.FromHandle(handle);
                var buttons = tray.FindAll(TreeScope.Descendants, new OrCondition(
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "ControlCenterButton"),
                    new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.AccentButton"),
                    new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.OmniButtonRight"),
                    new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.OmniButtonLeft")));
                foreach (AutomationElement button in buttons)
                {
                    var bounds = button.Current.BoundingRectangle;
                    if (!button.Current.IsOffscreen && !bounds.IsEmpty && bounds.Width > 0)
                    {
                        bounds.Inflate(2, 0);
                        list.Add(new(handle, bounds));
                    }
                }
            }
            targets = list.ToArray();
            StatusChanged?.Invoke();
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            targets = Array.Empty<Target>();
            StatusChanged?.Invoke();
        }
        finally
        {
            Volatile.Write(ref refreshing, 0);
            if (!disposed && targets.Length == 0 && startupRetries < 5)
            {
                try { refresh.Change(600 * ++startupRetries, Timeout.Infinite); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    private IntPtr MouseHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var msg = message.ToInt32();
            if (msg == 0x201)
            {
                dragStart = Marshal.PtrToStructure<Native.MouseData>(data).Point;
                leftDown = true;
                dragOffered = false;
                dragInTarget = false;
            }
            else if (msg == 0x200 && leftDown && !mouseCaptured)
            {
                var point = Marshal.PtrToStructure<Native.MouseData>(data).Point;
                if (Math.Abs(point.X - dragStart.X) + Math.Abs(point.Y - dragStart.Y) > 12)
                {
                    // Reuse the exact, cached Quick Settings hit rectangles used for clicks.
                    // Hook callbacks do no UI Automation, clipboard reads, or file I/O.
                    var inside = false;
                    Rect? anchor = null;
                    foreach (var target in targets)
                        if (target.Bounds.Contains(point.X, point.Y)) { inside = true; anchor = target.Bounds; break; }
                    if (!inside)
                        foreach (var corner in dropCorners)
                            if (corner.Contains(point.X, point.Y)) { inside = true; break; }
                    var now = Environment.TickCount64;
                    if (inside && (!dragInTarget || now - lastDropOffer > 400))
                    {
                        dragOffered = true;
                        lastDropOffer = now;
                        DragTargetEntered?.Invoke(point.X, point.Y, anchor);
                    }
                    dragInTarget = inside;
                }
            }
            if (msg == 0x202)
            {
                leftDown = false;
                if (dragOffered) DragReleased?.Invoke();
                dragOffered = false;
                dragInTarget = false;
            }
            if (msg == 0x202 && mouseCaptured)
            {
                mouseCaptured = false;
                ToggleRequested?.Invoke();
                return (IntPtr)1;
            }
            if (enabled && msg == 0x201 && !Native.Pressed(0x10))
            {
                var mouse = Marshal.PtrToStructure<Native.MouseData>(data);
                foreach (var target in targets)
                {
                    if (target.Bounds.Contains(mouse.Point.X, mouse.Point.Y) &&
                        Native.GetAncestor(Native.WindowFromPoint(mouse.Point), 2) == target.Tray)
                    {
                        mouseCaptured = true;
                        return (IntPtr)1;
                    }
                }
            }
        }
        return Native.CallNextHookEx(mouseHook, code, message, data);
    }

    private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<Native.KeyboardData>(data);
            var downEvent = message.ToInt32() is 0x100 or 0x104;
            if (downEvent && (key.Key == 0x2C || (key.Key == 0x53 && Native.Pressed(0x10) &&
                (Native.Pressed(0x5B) || Native.Pressed(0x5C)))))
                ScreenshotRequested?.Invoke();
            if (downEvent && key.Key == 0x1B && dragOffered)
            {
                DragReleased?.Invoke();
                dragOffered = false; leftDown = false; dragInTarget = false;
            }
            if (key.Key == 0x41)
            {
                var down = message.ToInt32() is 0x100 or 0x104;
                if (!down && keyCaptured) { keyCaptured = false; return (IntPtr)1; }
                if (down && (keyCaptured || (enabled && (Native.Pressed(0x5B) || Native.Pressed(0x5C)) &&
                    !Native.Pressed(0x10) && !Native.Pressed(0x11) && !Native.Pressed(0x12))))
                {
                    if (!keyCaptured)
                    {
                        keyCaptured = true;
                        Native.MaskWindowsKey();
                        ToggleRequested?.Invoke();
                    }
                    return (IntPtr)1;
                }
            }
        }
        return Native.CallNextHookEx(keyboardHook, code, message, data);
    }

    public void Dispose()
    {
        disposed = true;
        enabled = false;
        refresh.Dispose();
        if (layoutHook != IntPtr.Zero) Native.UnhookWinEvent(layoutHook);
        if (threadId != 0) Native.PostThreadMessage(threadId, 0x12, IntPtr.Zero, IntPtr.Zero);
        thread.Join(1000);
    }
}
