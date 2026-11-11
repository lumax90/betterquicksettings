using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterQuickSettings.Core;
using BetterQuickSettings.Interop;

namespace BetterQuickSettings.Services;

/// <summary>Event-driven clipboard access on its own STA; delayed rendering never blocks the panel.</summary>
public sealed class ClipboardHistoryService : IDisposable
{
    // Retain the pre-release clipboard marker to avoid duplicating restored items.
    internal const string OriginFormat = "Ada.ClipboardHistory.Origin";
    private readonly ClipboardHistoryStore store;
    private readonly Dispatcher ui;
    private readonly Thread thread;
    private readonly TaskCompletionSource<Dispatcher> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Dispatcher? worker;
    private DispatcherTimer? readTimer;
    private uint lastSequence;
    private uint copiedSequence;
    private int attempts;
    private bool reading;
    private volatile bool enabled, disposed;
    public event Action? Changed;
    public event Action<string>? Failed;

    public ClipboardHistoryService(ClipboardHistoryStore store, Dispatcher ui, bool enabled)
    {
        this.store = store; this.ui = ui; this.enabled = enabled;
        thread = new Thread(Run) { IsBackground = true, Name = "BetterQuickSettings clipboard history" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public Task Ready => ready.Task;
    internal uint CopiedSequence => Volatile.Read(ref copiedSequence);
    public bool Enabled
    {
        get => enabled;
        set
        {
            enabled = value;
            var dispatcher = worker;
            if (dispatcher is null || disposed || dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(new Action(() =>
            {
                readTimer?.Stop();
                lastSequence = Native.GetClipboardSequenceNumber();
            }));
        }
    }

    private void Run()
    {
        HwndSource? source = null;
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            worker = dispatcher;
            readTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(35), DispatcherPriority.Background,
                (_, _) => ReadClipboard(), dispatcher);
            readTimer.Stop();
            source = new HwndSource(new HwndSourceParameters("BetterQuickSettings clipboard listener")
            {
                ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0,
                HwndSourceHook = WindowMessage
            });
            lastSequence = Native.GetClipboardSequenceNumber();
            if (!Native.AddClipboardFormatListener(source.Handle))
                throw new InvalidOperationException("Could not connect to clipboard history updates.");
            ready.TrySetResult(dispatcher);
            if (!disposed) Dispatcher.Run();
        }
        catch (Exception ex) { ready.TrySetException(ex); PublishFailure("Clipboard history is unavailable: " + ex.Message); }
        finally
        {
            readTimer?.Stop();
            if (source is not null) { Native.RemoveClipboardFormatListener(source.Handle); source.Dispose(); }
        }
    }

    private IntPtr WindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x031D && enabled && !disposed && Native.GetClipboardSequenceNumber() != lastSequence)
        {
            attempts = 0;
            if (readTimer?.IsEnabled == false) readTimer.Start();
        }
        return IntPtr.Zero;
    }

    private void ReadClipboard()
    {
        readTimer!.Stop();
        if (disposed || !enabled || reading) return;
        var sequence = Native.GetClipboardSequenceNumber();
        if (sequence == lastSequence) return;
        reading = true;
        try
        {
            var data = Clipboard.GetDataObject();
            ClipboardEntry? added = null;
            if (data is not null && !Excluded(data))
            {
                if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text)
                {
                    if (StillCurrent(sequence)) added = store.AddTextAsync(text).GetAwaiter().GetResult();
                }
                else
                {
                    var image = ReadImage(data);
                    if (image is not null && StillCurrent(sequence)) added = SaveImage(image);
                }
            }
            lastSequence = sequence;
            if (added is not null) PublishChanged();
        }
        catch (ExternalException)
        {
            if (++attempts < 6 && !disposed && enabled) readTimer.Start();
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
        { lastSequence = sequence; }
        catch (Exception ex)
        {
            lastSequence = sequence;
            PublishFailure("Could not save clipboard history: " + ex.Message);
        }
        finally
        {
            reading = false;
            if (!disposed && enabled && Native.GetClipboardSequenceNumber() != sequence) readTimer.Start();
        }
    }

    private bool StillCurrent(uint sequence) => !disposed && enabled && sequence == Native.GetClipboardSequenceNumber();

    private static bool Excluded(IDataObject data)
    {
        if (data.GetDataPresent(OriginFormat, false) ||
            data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing", false)) return true;
        if (!data.GetDataPresent("CanIncludeInClipboardHistory", false)) return false;
        return data.GetData("CanIncludeInClipboardHistory", false) switch
        {
            MemoryStream stream when stream.Length >= 4 => BitConverter.ToUInt32(stream.ToArray(), 0) == 0,
            byte[] bytes when bytes.Length >= 4 => BitConverter.ToUInt32(bytes, 0) == 0,
            int number => number == 0,
            uint number => number == 0,
            _ => false
        };
    }

    private static BitmapSource? ReadImage(IDataObject data)
    {
        BitmapSource? image = null;
        if (data.GetDataPresent("PNG", false) && data.GetData("PNG", false) is MemoryStream png)
        {
            using (png)
            {
                if (png.Length > ClipboardHistoryStore.ImageByteLimit) return null;
                png.Position = 0;
                image = BitmapFrame.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            }
        }
        else if (data.GetDataPresent(DataFormats.Bitmap)) image = data.GetData(DataFormats.Bitmap) as BitmapSource;
        if (image is null || image.PixelWidth <= 0 || image.PixelHeight <= 0 ||
            (long)image.PixelWidth * image.PixelHeight > ClipboardHistoryStore.ImagePixelLimit) return null;
        return image;
    }

    private ClipboardEntry? SaveImage(BitmapSource image)
    {
        var pixels = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digest.AppendData(BitConverter.GetBytes(pixels.PixelWidth));
        digest.AppendData(BitConverter.GetBytes(pixels.PixelHeight));
        var stride = checked(pixels.PixelWidth * 4);
        var row = new byte[stride];
        for (var y = 0; y < pixels.PixelHeight; y++)
        {
            pixels.CopyPixels(new Int32Rect(0, y, pixels.PixelWidth, 1), row, stride, 0);
            digest.AppendData(row);
        }
        using var png = new LimitedImageStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(pixels));
        try { encoder.Save(png); }
        catch (Exception) when (png.LimitReached) { return null; }
        if (!enabled || disposed) return null;
        return store.AddImageAsync(png, Convert.ToHexString(digest.GetHashAndReset()),
            pixels.PixelWidth, pixels.PixelHeight).GetAwaiter().GetResult();
    }

    public Task CopyAsync(Guid id) => OnWorkerAsync(() =>
    {
        var entry = store.LoadAsync().GetAwaiter().GetResult().FirstOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException("This clipboard item is no longer available.");
        var data = new DataObject();
        using var png = entry.Kind == ClipboardItemKind.Image ? new MemoryStream(File.ReadAllBytes(store.ImagePath(id))) : null;
        if (entry.Kind == ClipboardItemKind.Text) data.SetText(entry.Text!, TextDataFormat.UnicodeText);
        else
        {
            var image = BitmapFrame.Create(png!, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            image.Freeze();
            png!.Position = 0;
            data.SetData("PNG", png, false);
            data.SetImage(image);
        }
        data.SetData(OriginFormat, id.ToString(), false);
        // Flush once so text and pixels remain pasteable after the application exits.
        Clipboard.SetDataObject(data, true);
        lastSequence = Native.GetClipboardSequenceNumber();
        Volatile.Write(ref copiedSequence, lastSequence);
        store.PromoteAsync(id).GetAwaiter().GetResult();
        PublishChanged();
    });

    public Task ClearAsync() => OnWorkerAsync(() =>
    {
        readTimer?.Stop();
        lastSequence = Native.GetClipboardSequenceNumber();
        // Serialized after any in-flight capture; clearing cannot resurrect that capture.
        store.ClearAsync().GetAwaiter().GetResult();
        PublishChanged();
    });

    private async Task OnWorkerAsync(Action action)
    {
        var dispatcher = await ready.Task;
        ObjectDisposedException.ThrowIf(disposed || dispatcher.HasShutdownStarted, this);
        await dispatcher.InvokeAsync(action, DispatcherPriority.Normal);
    }

    private void PublishChanged()
    {
        if (!disposed && !ui.HasShutdownStarted) ui.BeginInvoke(new Action(() => { if (!disposed) Changed?.Invoke(); }));
    }
    private void PublishFailure(string message)
    {
        if (!disposed && !ui.HasShutdownStarted) ui.BeginInvoke(new Action(() => { if (!disposed) Failed?.Invoke(message); }));
    }

    public void Dispose()
    {
        disposed = true;
        var dispatcher = worker;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted) dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
    }

    private sealed class LimitedImageStream : MemoryStream
    {
        public bool LimitReached { get; private set; }
        private void CheckLength(long size)
        {
            if (size <= ClipboardHistoryStore.ImageByteLimit) return;
            LimitReached = true;
            throw new IOException("The clipboard image exceeds the history item limit.");
        }
        public override void Write(byte[] buffer, int offset, int count) { CheckLength(Position + count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { CheckLength(Position + buffer.Length); base.Write(buffer); }
        public override void SetLength(long value) { CheckLength(value); base.SetLength(value); }
    }
}
