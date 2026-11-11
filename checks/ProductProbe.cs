using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterQuickSettings.Interop;
using BetterQuickSettings.Services;
using BetterQuickSettings.UI;

namespace BetterQuickSettings.Checks;

internal static class ProductProbe
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);

    internal static async Task RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        Native.GetCursorPos(out var cursor);
        var panel = new MainWindow(new LocalStore(Path.Combine(output, "isolated-data"))) { Title = "BetterQuickSettings product validation" };
        ((CheckBox)panel.FindName("PinCheck")).IsChecked = true;
        Window? target = null;
        try
        {
            await panel.Model.ShelfStore.LoadAsync();
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(40, 60, 105), Color.FromRgb(22, 156, 155), 30), null, new Rect(0, 0, 1280, 800));
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), null, new Rect(90, 160, 1100, 480), 32, 32);
                dc.DrawText(new FormattedText("Screenshot preview", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 48, Brushes.White, 1), new Point(145, 230));
            }
            var image = new RenderTargetBitmap(1280, 800, 96, 96, PixelFormats.Pbgra32);
            image.Render(drawing); image.Freeze();
            using var png = new MemoryStream();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(png);
            var record = await panel.Model.ShelfStore.AddScreenshotAsync(png, "product-probe-image");
            panel.OpenPanel();
            await panel.Model.InitializeShelfAsync(new System.Windows.Interop.WindowInteropHelper(panel).Handle, captureMode: true);
            panel.Model.Note = "Weekend ideas\nA short walk and coffee."; panel.Model.AddNote();
            panel.Model.Note = "Send the mockups\nInclude the compact layout."; panel.Model.AddNote();
            ((RadioButton)panel.FindName("NotesTab")).IsChecked = true;
            await Task.Delay(250);
            var notesLayout = panel.TransitionState;
            var list = (ItemsControl)panel.FindName("NotesList");
            var viewport = Find<ScrollViewer>(panel, element => element.Content == list);
            var noteLayout = new { ListHeight = list.ActualHeight, ViewportHeight = viewport?.ActualHeight, Extent = viewport?.ExtentHeight, Viewport = viewport?.ViewportHeight, PageHost = ((FrameworkElement)panel.FindName("PageHost")).ActualHeight, ContentHeight = ((FrameworkElement)panel.FindName("PanelContent")).ActualHeight };
            var pageHost = (FrameworkElement)panel.FindName("PageHost");
            var pageClip = VisualTreeHelper.GetClip(pageHost);
            var notesVisible = pageClip is null || pageClip.Bounds.Height >= pageHost.ActualHeight - 1;
            var clips = new List<object>();
            for (DependencyObject? node = list; node is Visual; node = VisualTreeHelper.GetParent(node))
                if (node is FrameworkElement element) clips.Add(new { Type = element.GetType().Name, element.Name, element.ActualHeight, Clip = VisualTreeHelper.GetClip(element)?.Bounds.ToString() });
            WindowCapture.Save(panel, Path.Combine(output, "notes.png"));
            panel.Capture(Path.Combine(output, "notes-wpf.png"));
            ((RadioButton)panel.FindName("FilesTab")).IsChecked = true;
            await Task.Delay(250);
            var tile = Find<Border>(panel, element => element.Name == "ShotTile") ?? throw new InvalidOperationException("Screenshot tile missing.");
            var actions = Find<StackPanel>(tile, element => element.Name == "ShotActions")!;
            SetCursorPos(50, 50); await Task.Delay(80);
            var actionsHidden = actions.Opacity == 0;
            var point = tile.PointToScreen(new Point(25, 40));
            SetCursorPos((int)point.X, (int)point.Y); await Task.Delay(80);
            var actionsVisible = actions.Opacity == 1;
            WindowCapture.Save(panel, Path.Combine(output, "screenshots.png"));
            mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(450);
            var viewer = Application.Current.Windows.OfType<ImagePreviewWindow>().FirstOrDefault();
            var previewOpened = viewer is not null && viewer.IsVisible && ((Image)viewer.FindName("Picture")).Source is BitmapSource bitmap && bitmap.PixelWidth == 1280;
            if (viewer is not null) { WindowCapture.Save(viewer, Path.Combine(output, "image-viewer.png")); viewer.Close(); }

            panel.OpenPanel(); await Task.Delay(180);
            tile = Find<Border>(panel, element => element.Name == "ShotTile")!;
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            target = new Window
            {
                Title = "BetterQuickSettings drag destination", Width = 230, Height = 160, WindowStyle = WindowStyle.None,
                ShowInTaskbar = false, ShowActivated = false, AllowDrop = true, Topmost = true,
                Left = panel.Left - 260, Top = panel.Top + 40,
                Content = new Border { Background = new SolidColorBrush(Color.FromRgb(35, 49, 67)), Child = new TextBlock { Text = "Drop target", Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } }
            };
            target.DragOver += (_, args) => { args.Effects = DragDropEffects.Copy; args.Handled = true; };
            target.Drop += (_, args) =>
            {
                if (args.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0) received.TrySetResult(paths[0]);
                args.Effects = DragDropEffects.Copy; args.Handled = true;
            };
            target.Show(); panel.Activate();
            var start = tile.PointToScreen(new Point(25, 40));
            var end = target.PointToScreen(new Point(100, 80));
            bool ghostVisible = false;
            var driver = Task.Run(() =>
            {
                SetCursorPos((int)start.X, (int)start.Y); System.Threading.Thread.Sleep(80);
                mouse_event(2, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(50);
                for (var step = 1; step <= 10; step++)
                {
                    SetCursorPos((int)(start.X + (end.X - start.X) * step / 10), (int)(start.Y + (end.Y - start.Y) * step / 10));
                    System.Threading.Thread.Sleep(35);
                    ghostVisible |= Native.FindWindow("Static", "BetterQuickSettings drag image") != IntPtr.Zero;
                }
                mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            });
            await driver;
            string delivered;
            try { delivered = await received.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch
            {
                File.WriteAllText(Path.Combine(output, "drag-state.txt"), panel.DragDiagnostic + "\n" + panel.Model.Notice);
                throw;
            }
            var exported = delivered == panel.Model.ShelfStore.GetPath(record) && File.Exists(delivered);
            var renderedGhost = DragThumbnail.Render(image);
            var pixels = new byte[renderedGhost.PixelWidth * renderedGhost.PixelHeight * 4];
            renderedGhost.CopyPixels(pixels, renderedGhost.PixelWidth * 4, 0);
            var transparentCorner = pixels[3] == 0;
            var pass = actionsHidden && actionsVisible && previewOpened && ghostVisible && exported && transparentCorner && notesVisible;
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
            { Pass = pass, ActionsHidden = actionsHidden, ActionsVisibleOnHover = actionsVisible, SingleClickPreview = previewOpened,
                RoundedDragThumbnail = ghostVisible && transparentCorner, RealFileDrop = exported,
                NoteCount = panel.Model.Notes.Count, NotesVisible = notesVisible, NotesLayout = notesLayout, NoteLayout = noteLayout, Clips = clips }, new JsonSerializerOptions { WriteIndented = true }));
            if (!pass) throw new InvalidOperationException("Product validation failed; inspect report.json.");
        }
        finally
        {
            mouse_event(4, 0, 0, 0, UIntPtr.Zero); SetCursorPos(cursor.X, cursor.Y);
            foreach (var viewer in Application.Current.Windows.OfType<ImagePreviewWindow>().ToArray()) viewer.Close();
            target?.Close(); panel.Cleanup(); panel.Close();
        }
    }

    private static T? Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        if (root is T item && match(item)) return item;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find(VisualTreeHelper.GetChild(root, i), match) is { } found) return found;
        return null;
    }
}
