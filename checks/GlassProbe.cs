// This file is linked into the app only for its opt-in --glass-check diagnostic.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterQuickSettings.Services;
using BetterQuickSettings.UI;
using BetterQuickSettings.Interop;
using MediaColor = System.Windows.Media.Color;

namespace BetterQuickSettings.Checks;

internal static class GlassProbe
{
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);

    internal static async Task RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        var panel = new MainWindow(new LocalStore(Path.Combine(output, "isolated-data")))
            { Title = "BetterQuickSettings glass validation" };
        ((CheckBox)panel.FindName("PinCheck")).IsChecked = true;
        var canvas = new Grid();
        canvas.ColumnDefinitions.Add(new ColumnDefinition());
        canvas.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new Border();
        var right = new Border();
        Grid.SetColumn(right, 1);
        canvas.Children.Add(left); canvas.Children.Add(right);
        var background = new Window
        {
            Title = "BetterQuickSettings glass test background", WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            Content = canvas
        };
        void SetColors(bool reverse)
        {
            var warm = new SolidColorBrush(MediaColor.FromRgb(237, 143, 69));
            var cool = new SolidColorBrush(MediaColor.FromRgb(48, 105, 236));
            left.Background = reverse ? cool : warm;
            right.Background = reverse ? warm : cool;
        }
        try
        {
            panel.Show(); panel.UpdateLayout(); panel.PositionPanel();
            // Keep transient system notification toasts out of the sampled glass area.
            var work = Native.DisplayAtCursor().WorkingArea;
            var transform = PresentationSource.FromVisual(panel)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var testOrigin = transform.Transform(new Point(work.Left + 80, work.Top + 80));
            panel.Left = testOrigin.X; panel.Top = testOrigin.Y;
            background.Left = panel.Left - 20; background.Top = panel.Top - 20;
            background.Width = panel.ActualWidth + 40; background.Height = panel.ActualHeight + 40;
            SetColors(false); background.Show(); panel.Activate(); Raise(panel);
            await Task.Delay(500);
            var activeWarm = WindowCapture.Save(panel, Path.Combine(output, "active-warm.png"));
            WindowCapture.Save(background, Path.Combine(output, "test-background.png"));
            var activeConfirmed = panel.IsActive;
            SetColors(true); await Task.Delay(350);
            var activeCool = WindowCapture.Save(panel, Path.Combine(output, "active-cool.png"));

            background.Activate(); Raise(panel); SetColors(false); await Task.Delay(350);
            var inactiveWarm = WindowCapture.Save(panel, Path.Combine(output, "inactive-warm.png"));
            var inactiveConfirmed = !panel.IsActive && panel.IsVisible;
            SetColors(true); await Task.Delay(350);
            var inactiveCool = WindowCapture.Save(panel, Path.Combine(output, "inactive-cool.png"));

            for (var i = 0; i < 3; i++) GlassBackdrop.Apply(panel, captureMode: false, force: true);
            await Task.Delay(250);
            var reapplied = WindowCapture.Save(panel, Path.Combine(output, "reapplied.png"));
            var activeDifference = Difference(activeWarm, activeCool);
            var inactiveDifference = Difference(inactiveWarm, inactiveCool);
            var reapplyDifference = Difference(inactiveCool, reapplied);
            panel.Model.GlassAmount = 100;
            SetColors(false); await Task.Delay(250);
            var clearWarm = WindowCapture.Save(panel, Path.Combine(output, "clear-warm.png"));
            SetColors(true); await Task.Delay(250);
            var clearCool = WindowCapture.Save(panel, Path.Combine(output, "clear-cool.png"));
            var clearDifference = Difference(clearWarm, clearCool);
            panel.Model.GlassAmount = 0;
            SetColors(false); await Task.Delay(200);
            var opaqueWarm = WindowCapture.Save(panel, Path.Combine(output, "opaque-warm.png"));
            SetColors(true); await Task.Delay(200);
            var opaqueCool = WindowCapture.Save(panel, Path.Combine(output, "opaque-cool.png"));
            var opaqueDifference = Difference(opaqueWarm, opaqueCool);
            var pass = activeConfirmed && inactiveConfirmed && activeDifference > 5 && inactiveDifference > 5 &&
                reapplyDifference < 3 && clearDifference > activeDifference + 5 && opaqueDifference < 3;
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
            {
                Pass = pass, ActiveConfirmed = activeConfirmed, InactiveConfirmed = inactiveConfirmed,
                ActiveBackgroundResponse = activeDifference, InactiveBackgroundResponse = inactiveDifference,
                ClearBackgroundResponse = clearDifference, OpaqueBackgroundResponse = opaqueDifference,
                ReapplyDifference = reapplyDifference, RenderingTier = RenderCapability.Tier >> 16,
                PanelBackground = panel.Background.ToString(),
                TargetBackground = HwndSource.FromHwnd(new WindowInteropHelper(panel).Handle)?.CompositionTarget.BackgroundColor.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (!pass) throw new InvalidOperationException("Glass validation failed; inspect report.json and captures.");
        }
        finally { panel.Cleanup(); panel.Close(); background.Close(); }
    }

    private static void Raise(Window window) =>
        SetWindowPos(new WindowInteropHelper(window).Handle, new IntPtr(-1), 0, 0, 0, 0, 0x13);

    private static double Difference(BitmapSource a, BitmapSource b)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return 255;
        var stride = a.PixelWidth * 4;
        var first = new byte[stride * a.PixelHeight];
        var second = new byte[first.Length];
        a.CopyPixels(first, stride, 0); b.CopyPixels(second, stride, 0);
        long total = 0, count = 0;
        for (var y = 12; y < a.PixelHeight - 12; y += 3)
            for (var x = 12; x < a.PixelWidth - 12; x += 3)
            {
                var offset = y * stride + x * 4;
                total += Math.Abs(first[offset] - second[offset]) + Math.Abs(first[offset + 1] - second[offset + 1]) + Math.Abs(first[offset + 2] - second[offset + 2]);
                count += 3;
            }
        return Math.Round((double)total / Math.Max(count, 1), 3);
    }
}
