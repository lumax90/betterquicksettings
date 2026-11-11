using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterQuickSettings.Interop;

/// <summary>A single cached, click-through image follows the pointer only during an OLE drag.</summary>
internal sealed class DragThumbnail : IDisposable
{
    private IntPtr window;
    private long lastMove;
    private Native.Point previous;
    public IntPtr Handle => window;

    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct Blend { public byte Operation, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, Bits;
        public uint Compression, ImageSize; public int X, Y; public uint Colors, Important;
    }

    public DragThumbnail(IntPtr owner, BitmapSource image, double dpi)
    {
        var pixels = Render(image, dpi);
        var size = new Size { Width = pixels.PixelWidth, Height = pixels.PixelHeight };
        const uint extended = 0x80000 | 0x20 | 0x08000000 | 0x80;
        window = CreateWindowEx(extended, "Static", "BetterQuickSettings drag image", 0x80000000, 0, 0,
            size.Width, size.Height, owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero) throw new InvalidOperationException("Could not create the drag preview.");
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var info = new BitmapInfo { Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = size.Width, Height = -size.Height, Planes = 1, Bits = 32 };
        var bitmap = CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(memory, bitmap);
        try
        {
            if (bits == IntPtr.Zero) throw new InvalidOperationException("Could not prepare the drag image.");
            var stride = size.Width * 4;
            var buffer = new byte[stride * size.Height];
            pixels.CopyPixels(buffer, stride, 0);
            Marshal.Copy(buffer, 0, bits, buffer.Length);
            Native.GetCursorPos(out var pointer);
            var destination = new Native.Point { X = pointer.X + 14, Y = pointer.Y + 16 };
            var source = new Native.Point();
            var blend = new Blend { Alpha = 242, Format = 1 };
            if (!UpdateLayeredWindow(window, screen, ref destination, ref size, memory, ref source, 0, ref blend, 2))
                throw new InvalidOperationException("Could not show the drag image.");
            ShowWindow(window, 4);
            FollowPointer(force: true);
        }
        catch { Dispose(); throw; }
        finally { SelectObject(memory, old); DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(IntPtr.Zero, screen); }
    }

    public void FollowPointer(bool force = false)
    {
        if (window == IntPtr.Zero || (!force && Environment.TickCount64 - lastMove < 8)) return;
        Native.GetCursorPos(out var point);
        if (!force && point.X == previous.X && point.Y == previous.Y) return;
        previous = point; lastMove = Environment.TickCount64;
        SetWindowPos(window, new IntPtr(-1), point.X + 14, point.Y + 16, 0, 0, 0x211);
    }

    internal static BitmapSource Render(BitmapSource image, double dpi = 1)
    {
        var scale = Math.Min(120d / image.PixelWidth, 90d / image.PixelHeight);
        var width = Math.Max(12, image.PixelWidth * scale);
        var height = Math.Max(12, image.PixelHeight * scale);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            for (var i = 7; i >= 1; i--)
                drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(11 - i), 0, 0, 0)), null,
                    new Rect(8 - i, 9 - i, width + i * 2, height + i * 2), 9 + i, 9 + i);
            var bounds = new Rect(8, 8, width, height);
            drawing.PushClip(new RectangleGeometry(bounds, 9, 9));
            drawing.DrawImage(image, bounds);
            drawing.Pop();
        }
        var result = new RenderTargetBitmap((int)Math.Ceiling((width + 16) * dpi), (int)Math.Ceiling((height + 16) * dpi),
            96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        result.Render(visual); result.Freeze();
        return result;
    }

    public void Dispose()
    {
        if (window == IntPtr.Zero) return;
        DestroyWindow(window); window = IntPtr.Zero;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint extended, string name, string title, uint style, int x, int y, int width, int height, IntPtr owner, IntPtr menu, IntPtr instance, IntPtr data);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr target, ref Native.Point position, ref Size size, IntPtr source, ref Native.Point origin, uint key, ref Blend blend, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
