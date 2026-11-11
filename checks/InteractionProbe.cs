using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Threading;
using BetterQuickSettings.Interop;
using BetterQuickSettings.Services;
using BetterQuickSettings.UI;

namespace BetterQuickSettings.Checks;

internal static class InteractionProbe
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out uint value, int size);

    internal static async Task RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        var errors = new List<string>();
        DispatcherUnhandledExceptionEventHandler onError = (_, args) => { errors.Add(args.Exception.ToString()); args.Handled = true; };
        Application.Current.DispatcherUnhandledException += onError;
        Native.GetCursorPos(out var cursor);
        var panel = new MainWindow(new LocalStore(Path.Combine(output, "isolated-data"))) { Title = "BetterQuickSettings interaction validation" };
        panel.Model.ToolsExpanded = false;
        ((CheckBox)panel.FindName("PinCheck")).IsChecked = true;
        try
        {
            panel.OpenPanel();
            var startTop = panel.SurfaceOffset;
            await Task.Delay(70);
            var middleTop = panel.SurfaceOffset;
            await Task.Delay(220);
            var endTop = panel.SurfaceOffset;
            var controlsHeight = panel.VisibleSurfaceHeight;
            var preferences = (Button)panel.FindName("PreferencesButton");
            preferences.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var transitionStart = panel.TransitionState;
            await Task.Delay(70);
            var middleHeight = panel.VisibleSurfaceHeight;
            await Task.Delay(220);
            var preferencesHeight = panel.VisibleSurfaceHeight;
            var transitionEnd = panel.TransitionState;

            var glass = (Slider)panel.FindName("GlassSlider");
            glass.ApplyTemplate();
            var progress = (ProgressBar)glass.Template.FindName("FillProgress", glass);
            await DragAsync(glass, 0.10, 0.85);
            var firstDrag = glass.Value;
            await DragAsync(glass, 0.95, 0.30);
            var secondDrag = glass.Value;
            await Task.Delay(200);
            var visualMatches = Math.Abs(progress.Value - glass.Value) < 0.1;

            var beforeKey = glass.Value;
            keybd_event(0x27, 0, 0, UIntPtr.Zero);
            await Task.Delay(30);
            keybd_event(0x27, 0, 2, UIntPtr.Zero);
            await Task.Delay(180);
            var keyboardWorks = glass.Value > beforeKey;

            // Mouse focus must not leave the old always-on outline.
            var buttonPoint = preferences.PointToScreen(new Point(preferences.ActualWidth / 2, preferences.ActualHeight / 2));
            SetCursorPos((int)buttonPoint.X, (int)buttonPoint.Y);
            await Task.Delay(100);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(50);
            mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(180);
            var adorners = AdornerLayer.GetAdornerLayer(preferences)?.GetAdorners(preferences);
            var mouseFocusClean = adorners is null || adorners.Length == 0;
            var frame = (Border)panel.FindName("PanelSurface");
            var borderQuery = DwmGetWindowAttribute(new WindowInteropHelper(panel).Handle, 34, out var frameColor, sizeof(uint));
            var nativeBorderDisabled = GlassBackdrop.NativeBorderDisabled(panel);

            panel.Model.GlassAmount = 0;
            await Task.Delay(150);
            var opaqueAtZero = panel.Background is System.Windows.Media.SolidColorBrush brush && brush.Color.A == 255;
            panel.Model.GlassAmount = 80;
            await Task.Delay(180);
            var transparentWhenEnabled = GlassBackdrop.IsComposited(panel);
            WindowCapture.Save(panel, Path.Combine(output, "preferences.png"));

            ((RadioButton)panel.FindName("ControlsTab")).IsChecked = true;
            await Task.Delay(220);
            var tools = (Expander)panel.FindName("ToolsExpander");
            var compactHeight = panel.VisibleSurfaceHeight;
            tools.IsExpanded = true;
            var toolsTransition = panel.TransitionState;
            await Task.Delay(70);
            var expandingHeight = panel.VisibleSurfaceHeight;
            await Task.Delay(220);
            var expandedHeight = panel.VisibleSurfaceHeight;
            WindowCapture.Save(panel, Path.Combine(output, "expanded.png"));
            tools.IsExpanded = false;
            await Task.Delay(220);
            var compactAgain = Math.Abs(panel.VisibleSurfaceHeight - compactHeight) < 2;

            var motion = !UiMotion.Enabled || (startTop > endTop + 1 && middleTop >= endTop && middleTop < startTop);
            var stableFrame = Math.Abs(controlsHeight - preferencesHeight) < 1 && Math.Abs(middleHeight - controlsHeight) < 1 &&
                Math.Abs(expandingHeight - compactHeight) < 1 && Math.Abs(expandedHeight - compactHeight) < 1;
            var pass = firstDrag > 80 && secondDrag < 35 && visualMatches && keyboardWorks && mouseFocusClean &&
                frame.BorderThickness == new Thickness(0) && nativeBorderDisabled &&
                opaqueAtZero && transparentWhenEnabled && compactAgain && motion && stableFrame && errors.Count == 0;
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
            {
                Pass = pass, MotionEnabled = UiMotion.Enabled, StartTop = startTop, MiddleTop = middleTop, EndTop = endTop,
                ControlsHeight = controlsHeight, MiddleHeight = middleHeight, PreferencesHeight = preferencesHeight,
                CompactHeight = compactHeight, ExpandingHeight = expandingHeight, ExpandedHeight = expandedHeight,
                FirstDrag = firstDrag, SecondDrag = secondDrag, VisualMatches = visualMatches, KeyboardWorks = keyboardWorks,
                MouseFocusClean = mouseFocusClean, FrameBorderColor = frameColor, BorderQueryResult = borderQuery,
                NativeBorderDisabled = nativeBorderDisabled, OpaqueAtZero = opaqueAtZero,
                TransitionStart = transitionStart,
                TransitionEnd = transitionEnd,
                StableFrame = stableFrame,
                ToolsTransition = toolsTransition,
                TransparentWhenEnabled = transparentWhenEnabled, CompactAgain = compactAgain,
                FormsLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "System.Windows.Forms"),
                Errors = errors
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (!pass) throw new InvalidOperationException("Interaction validation failed; inspect report.json.");
        }
        finally
        {
            mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            SetCursorPos(cursor.X, cursor.Y);
            panel.Cleanup(); panel.Close();
            Application.Current.DispatcherUnhandledException -= onError;
        }
    }

    private static async Task DragAsync(Slider slider, double from, double to)
    {
        var first = slider.PointToScreen(new Point(slider.ActualWidth * from, slider.ActualHeight / 2));
        var last = slider.PointToScreen(new Point(slider.ActualWidth * to, slider.ActualHeight / 2));
        SetCursorPos((int)first.X, (int)first.Y);
        await Task.Delay(50);
        mouse_event(2, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(50);
        for (var step = 1; step <= 4; step++)
        {
            SetCursorPos((int)(first.X + (last.X - first.X) * step / 4), (int)last.Y);
            await Task.Delay(35);
        }
        mouse_event(4, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(150);
    }
}
