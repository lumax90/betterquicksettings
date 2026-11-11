using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BetterQuickSettings.Interop;

namespace BetterQuickSettings.UI;

public partial class DropHintWindow : Window
{
    private readonly DispatcherTimer hideTimer;
    private bool validDragInside;
    private bool finished;
    private long lastInside, finishedAt;
    private int dragOvers, acceptedOvers, drops;
    internal string DragDiagnostic => $"over={dragOvers}; accepted={acceptedOvers}; drops={drops}; valid={validDragInside}; finished={finished}";
    private double glassAmount = 55;
    public double GlassAmount
    {
        get => glassAmount;
        set { glassAmount = value; GlassBackdrop.Apply(this, captureMode: false, amount: value); }
    }
    public event Action<string[]>? FilesDropped;

    public DropHintWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            GlassBackdrop.Apply(this, captureMode: false, amount: GlassAmount);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
        };
        Loaded += (_, _) => GlassBackdrop.Apply(this, captureMode: false, amount: GlassAmount);
        hideTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background,
            (_, _) => CheckDismissal(), Dispatcher);
        hideTimer.Stop();
        Closed += (_, _) => hideTimer.Stop();
    }

    private IntPtr WindowMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0x031E or 0x001A or 0x031A or 0x0218)
            Dispatcher.BeginInvoke(new Action(() => GlassBackdrop.Apply(this, captureMode: false, force: true, amount: GlassAmount)));
        return IntPtr.Zero;
    }

    public void OfferAt(int x, int y, Rect? taskbarTarget = null)
    {
        if (!Native.Pressed(1)) return;
        if (IsVisible) { finished = false; return; }
        new WindowInteropHelper(this).EnsureHandle();
        var screen = Native.DisplayAt(new Native.Point { X = x, Y = y });
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new Point(screen.Bounds.Left, screen.Bounds.Top));
        var bottomRight = transform.Transform(new Point(screen.Bounds.Right, screen.Bounds.Bottom));
        var pointer = transform.Transform(new Point(x, y));
        var anchor = taskbarTarget is { } target
            ? transform.Transform(new Point(target.Left + target.Width / 2, target.Bottom))
            : new Point(pointer.X, pointer.Y + 18);
        // Include the hovered taskbar button in the target: dropping in place is sufficient.
        Left = Math.Clamp(anchor.X - Width / 2, topLeft.X + 6, bottomRight.X - Width - 6);
        Top = Math.Clamp(anchor.Y - Height, topLeft.Y + 6, bottomRight.Y - Height);
        validDragInside = false;
        finished = false;
        lastInside = Environment.TickCount64;
        Show();
        hideTimer.Start();
        if (TargetSurface.Child is FrameworkElement child) UiMotion.Reveal(child, 100, 2);
    }

    public void ScheduleHide()
    {
        if (!finished) { finished = true; finishedAt = Environment.TickCount64; }
        if (IsVisible) hideTimer.Start();
    }

    private void CheckDismissal()
    {
        if (!IsVisible) { hideTimer.Stop(); return; }
        Native.GetCursorPos(out var cursor);
        var point = PointFromScreen(new Point(cursor.X, cursor.Y));
        var nearby = new Rect(-12, -12, ActualWidth + 24, ActualHeight + 24).Contains(point);
        if (!Native.Pressed(1)) ScheduleHide();
        // The low-level mouse hook sees release before the source's OLE loop delivers Drop.
        // Keep the HWND registered briefly; removing it on that first mouse-up races delivery.
        if ((finished && Environment.TickCount64 - finishedAt >= 300) ||
            (!finished && !validDragInside && !nearby && Environment.TickCount64 - lastInside > 200))
        {
            validDragInside = false;
            hideTimer.Stop();
            Hide();
        }
    }

    private void OnDragOver(object sender, DragEventArgs args)
    {
        dragOvers++;
        validDragInside = MainWindow.AcceptsFiles(args);
        if (validDragInside) acceptedOvers++;
        lastInside = Environment.TickCount64;
        args.Effects = validDragInside ? DragDropEffects.Copy : DragDropEffects.None;
        TargetSurface.BorderBrush = (Brush)FindResource(validDragInside ? "Accent" : "GlassEdge");
        args.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs args)
    {
        validDragInside = false;
        lastInside = Environment.TickCount64;
    }

    private void OnDrop(object sender, DragEventArgs args)
    {
        drops++;
        try
        {
            if (MainWindow.AcceptsFiles(args) && args.Data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                args.Effects = DragDropEffects.Copy;
                // Let OLE finish the source drag before opening the main panel.
                Dispatcher.BeginInvoke(new Action(() => FilesDropped?.Invoke(paths)), DispatcherPriority.Background);
            }
            else args.Effects = DragDropEffects.None;
        }
        finally
        {
            args.Handled = true;
            validDragInside = false;
            hideTimer.Stop();
            Hide();
        }
    }
}
