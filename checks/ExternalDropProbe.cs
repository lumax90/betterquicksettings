using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterQuickSettings.Interop;
using BetterQuickSettings.Services;

namespace BetterQuickSettings.Checks;

internal static class ExternalDropProbe
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int length);
    private delegate bool EnumProc(IntPtr window, IntPtr data);

    public static async Task ReceiveAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new Window
        {
            Title = "BetterQuickSettings external drop receiver", Width = 260, Height = 180, Left = 200, Top = 200,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, Topmost = true, AllowDrop = true,
            Background = new SolidColorBrush(Color.FromRgb(32, 48, 69)),
            Content = new TextBlock { Text = "External drop target", Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        window.DragOver += (_, args) => { args.Effects = DragDropEffects.Copy; args.Handled = true; };
        window.Drop += (_, args) =>
        {
            var paths = args.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
            File.WriteAllText(Path.Combine(directory, "started.txt"), "started");
            // Simulate a receiving application doing slow work before returning its OLE Drop call.
            System.Threading.Thread.Sleep(900);
            File.WriteAllText(Path.Combine(directory, "received.json"), JsonSerializer.Serialize(paths));
            args.Effects = DragDropEffects.Copy; args.Handled = true;
            done.TrySetResult();
        };
        window.Show(); window.UpdateLayout();
        var point = window.PointToScreen(new Point(130, 90));
        File.WriteAllText(Path.Combine(directory, "ready.json"), JsonSerializer.Serialize(new { point.X, point.Y }));
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { window.Close(); }
    }

    public static async Task RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        Native.GetCursorPos(out var cursor);
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Process path is unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        var receiver = Path.Combine(output, "receiver");
        start.ArgumentList.Add("--drop-receiver"); start.ArgumentList.Add(receiver);
        using var process = Process.Start(start)!;
        var panel = new MainWindow(new LocalStore(Path.Combine(output, "isolated-data"))) { Title = "BetterQuickSettings external drag validation" };
        ((CheckBox)panel.FindName("PinCheck")).IsChecked = true;
        try
        {
            var ready = Path.Combine(receiver, "ready.json");
            for (var i = 0; i < 100 && !File.Exists(ready); i++) await Task.Delay(50);
            using var point = JsonDocument.Parse(File.ReadAllText(ready));
            var endX = point.RootElement.GetProperty("X").GetDouble();
            var endY = point.RootElement.GetProperty("Y").GetDouble();
            var imagePath = Path.Combine(output, "sample.png");
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(Brushes.CornflowerBlue, null, new Rect(0, 0, 120, 80));
            var image = new RenderTargetBitmap(120, 80, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(imagePath)) png.Save(file);
            await panel.Model.InitializeShelfAsync(new System.Windows.Interop.WindowInteropHelper(panel).EnsureHandle(), true);
            await panel.Model.AddFilesAsync([imagePath]);
            panel.OpenPanel();
            ((RadioButton)panel.FindName("FilesTab")).IsChecked = true;
            await Task.Delay(250);
            var item = panel.Model.HeldFiles.Single();
            var source = Find(panel, element => element.DataContext == item && element is Border { Focusable: true })!;
            var origin = source.PointToScreen(new Point(18, 18));
            var ghostBeforeRelease = false;
            var ghostAfterRelease = true;
            var driver = Task.Run(() =>
            {
                SetCursorPos((int)origin.X, (int)origin.Y); System.Threading.Thread.Sleep(100);
                mouse_event(2, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(70);
                for (var step = 1; step <= 15; step++)
                {
                    SetCursorPos((int)(origin.X + (endX - origin.X) * step / 15), (int)(origin.Y + (endY - origin.Y) * step / 15));
                    System.Threading.Thread.Sleep(45);
                    ghostBeforeRelease |= HasDragImage();
                }
                mouse_event(4, 0, 0, 0, UIntPtr.Zero);
                System.Threading.Thread.Sleep(200);
                ghostAfterRelease = HasDragImage();
            });
            await driver;
            var received = Path.Combine(receiver, "received.json");
            for (var i = 0; i < 100 && !File.Exists(received); i++) await Task.Delay(50);
            var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(received))!;
            await Task.Delay(100);
            panel.Dismiss(); await Task.Delay(200); panel.TogglePanel(); await Task.Delay(140);
            var singleStart = panel.StartedDragCount == 1;
            var externalPassed = paths.Single() == item.FullPath && ghostBeforeRelease && !ghostAfterRelease && panel.IsVisible && !panel.IsFileDragInProgress;
            var emptyTarget = new Window { Title = "BetterQuickSettings rejected drop area", Width = 230, Height = 150, Left = 240, Top = 260,
                WindowStyle = WindowStyle.None, ShowInTaskbar = false, Topmost = true, ShowActivated = false,
                Background = new SolidColorBrush(Color.FromRgb(45, 53, 67)) };
            emptyTarget.Show(); panel.Activate();
            async Task<bool> ExtraDrop(bool cancel)
            {
                var count = panel.StartedDragCount;
                var begin = source.PointToScreen(new Point(18, 18));
                var finish = emptyTarget.PointToScreen(new Point(90, 80));
                await Task.Run(() =>
                {
                    SetCursorPos((int)begin.X, (int)begin.Y); System.Threading.Thread.Sleep(80);
                    mouse_event(2, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60);
                    for (var step = 1; step <= 10; step++)
                    {
                        SetCursorPos((int)(begin.X + (finish.X - begin.X) * step / 10), (int)(begin.Y + (finish.Y - begin.Y) * step / 10));
                        System.Threading.Thread.Sleep(35);
                    }
                    if (cancel) { keybd_event(0x1B, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(50); keybd_event(0x1B, 0, 2, UIntPtr.Zero); }
                    mouse_event(4, 0, 0, 0, UIntPtr.Zero);
                });
                await Task.Delay(100);
                var clean = !HasDragImage() && !panel.IsFileDragInProgress && panel.StartedDragCount == count + 1;
                panel.Dismiss(); await Task.Delay(200); panel.TogglePanel(); await Task.Delay(140);
                return clean && panel.IsVisible;
            }
            bool rejected, cancelled;
            try { rejected = await ExtraDrop(false); cancelled = await ExtraDrop(true); }
            finally { emptyTarget.Close(); }
            var pass = externalPassed && singleStart && rejected && cancelled;
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
            { Pass = pass, ExternalFileDrop = paths.Single() == item.FullPath, GhostDuringDrag = ghostBeforeRelease,
                GhostAfterRelease = ghostAfterRelease, ReopenedAfterDrop = panel.IsVisible, DragActiveAfterDrop = panel.IsFileDragInProgress,
                OneSessionPerGesture = singleStart, RejectedDropRecovered = rejected, EscapeRecovered = cancelled,
                panel.DragDiagnostic }, new JsonSerializerOptions { WriteIndented = true }));
            if (!pass) throw new InvalidOperationException("External drag validation failed.");
        }
        finally
        {
            mouse_event(4, 0, 0, 0, UIntPtr.Zero); SetCursorPos(cursor.X, cursor.Y);
            panel.Cleanup(); panel.Close();
            if (!process.HasExited) { process.Kill(); process.WaitForExit(); }
        }
    }

    private static bool HasDragImage()
    {
        var found = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var process);
            if (process != Environment.ProcessId) return true;
            var name = new System.Text.StringBuilder(128); GetClassName(window, name, name.Capacity);
            found |= name.ToString() == "Static";
            return true;
        }, IntPtr.Zero);
        return found;
    }
    private static FrameworkElement? Find(DependencyObject root, Func<FrameworkElement, bool> predicate)
    {
        if (root is FrameworkElement element && predicate(element)) return element;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find(VisualTreeHelper.GetChild(root, i), predicate) is { } found) return found;
        return null;
    }
}
