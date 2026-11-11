using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterQuickSettings.Interop;
using BetterQuickSettings.Services;

namespace BetterQuickSettings.UI;

public partial class ImagePreviewWindow : Window
{
    private BitmapSource? image;
    private FileStream? lease;
    private bool closed, fitting = true, panning;
    private double scale = 1, panX, panY;
    private Point panStart;

    public ImagePreviewWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            GlassBackdrop.Apply(this, captureMode: true, amount: 0);
            var display = Native.DisplayAtCursor();
            var matrix = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var origin = matrix.Transform(new Point(display.WorkingArea.Left, display.WorkingArea.Top));
            var size = matrix.Transform(new Vector(display.WorkingArea.Width, display.WorkingArea.Height));
            Width = Math.Min(960, size.X - 40); Height = Math.Min(680, size.Y - 40);
            Left = origin.X + (size.X - Width) / 2; Top = origin.Y + (size.Y - Height) / 2;
        };
        Viewport.SizeChanged += (_, _) => { if (fitting) Fit(); };
        Closed += (_, _) => { closed = true; Picture.Source = null; image = null; lease?.Dispose(); lease = null; };
    }

    public async Task LoadAsync(string path)
    {
        Title = Filename.Text = Path.GetFileName(path);
        try
        {
            lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoded = await Task.Run(() => ScreenshotService.LoadImage(path, thumbnail: false));
            if (closed) return;
            image = decoded;
            Picture.Source = decoded;
            Status.Visibility = Visibility.Collapsed;
            Fit();
        }
        catch (Exception) { if (!closed) Status.Text = "This image could not be opened."; }
    }

    private void Fit()
    {
        if (image is null || Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) return;
        fitting = true;
        SetScale(Math.Min((Viewport.ActualWidth - 18) / image.PixelWidth, (Viewport.ActualHeight - 18) / image.PixelHeight));
    }
    private void SetScale(double value)
    {
        if (image is null) return;
        scale = Math.Clamp(value, 0.02, 8);
        Picture.Width = image.PixelWidth * scale;
        Picture.Height = image.PixelHeight * scale;
    }
    private void OnWheel(object sender, MouseWheelEventArgs args)
    {
        fitting = false; SetScale(scale * (args.Delta > 0 ? 1.15 : 1 / 1.15)); args.Handled = true;
    }
    private void OnPanStart(object sender, MouseButtonEventArgs args)
    {
        for (var node = args.OriginalSource as DependencyObject; node is not null && node != Viewport;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is ScrollBar) return;
        panning = Viewport.CaptureMouse(); panStart = args.GetPosition(Viewport);
        panX = Viewport.HorizontalOffset; panY = Viewport.VerticalOffset; args.Handled = true;
    }
    private void OnPan(object sender, MouseEventArgs args)
    {
        if (!panning || args.LeftButton != MouseButtonState.Pressed) return;
        var point = args.GetPosition(Viewport);
        Viewport.ScrollToHorizontalOffset(panX + panStart.X - point.X);
        Viewport.ScrollToVerticalOffset(panY + panStart.Y - point.Y);
    }
    private void OnPanEnd(object sender, MouseButtonEventArgs args) { panning = false; Viewport.ReleaseMouseCapture(); }
    private void FitClick(object sender, RoutedEventArgs args) => Fit();
    private void ActualSizeClick(object sender, RoutedEventArgs args) { fitting = false; SetScale(1); }
    private void CloseClick(object sender, RoutedEventArgs args) => Close();
    private void OnKeyDown(object sender, KeyEventArgs args) { if (args.Key == Key.Escape) { Close(); args.Handled = true; } }
}
