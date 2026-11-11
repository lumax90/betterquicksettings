using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterQuickSettings.Interop;

namespace BetterQuickSettings.Services;

/// <summary>Observes new saved captures and image clipboard updates originating from capture tools.</summary>
public sealed class ScreenshotService : IDisposable
{
    private readonly FileShelfStore shelf;
    private readonly Dispatcher dispatcher;
    private readonly IntPtr window;
    private readonly List<FileSystemWatcher> watchers = new();
    private readonly HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim imageProcessing = new(1, 1);
    private readonly DispatcherTimer clipboardTimer;
    private DateTimeOffset captureUntil;
    private int captureRevision;
    private uint lastSequence;
    private int clipboardAttempts;
    private bool readingClipboard;
    private volatile bool disposed;
    public event Action? Changed;
    public event Action<string>? Failed;

    public ScreenshotService(FileShelfStore shelf, Dispatcher dispatcher, IntPtr window)
    {
        this.shelf = shelf; this.dispatcher = dispatcher; this.window = window;
        lastSequence = Native.GetClipboardSequenceNumber();
        clipboardTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(140), DispatcherPriority.Background,
            async (_, _) => await ReadClipboardAsync(), dispatcher);
        clipboardTimer.Stop();
        if (!Native.AddClipboardFormatListener(window))
            throw new InvalidOperationException("Could not connect to screenshot clipboard updates.");
    }

    public void ArmCapture()
    {
        captureRevision++;
        captureUntil = DateTimeOffset.UtcNow.AddMinutes(2);
    }
    public void ClipboardChanged()
    {
        if (disposed) return;
        clipboardAttempts = 0;
        clipboardTimer.Stop(); clipboardTimer.Start();
    }

    public void Watch(string? customFolder)
    {
        foreach (var watcher in watchers) watcher.Dispose();
        watchers.Clear();
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var folders = new[] { Native.ScreenshotsFolder(),
            string.IsNullOrEmpty(pictures) ? null : Path.Combine(pictures, "Screenshots"), customFolder }
            .Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>()
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            // Watching an existing parent also catches the first screenshot creating its directory.
            var watchRoot = Directory.Exists(folder) ? folder : Path.GetDirectoryName(folder);
            if (watchRoot is null || !Directory.Exists(watchRoot)) continue;
            try
            {
                var watcher = new FileSystemWatcher(watchRoot)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                void Queue(string path)
                {
                    if (path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) && IsImage(path)) QueueFile(path);
                }
                watcher.Created += (_, args) => Queue(args.FullPath);
                watcher.Changed += (_, args) => Queue(args.FullPath);
                watcher.Renamed += (_, args) => Queue(args.FullPath);
                watcher.Error += (_, _) => PublishFailure("Screenshot folder monitoring was interrupted. Re-select the folder to reconnect.");
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { PublishFailure("Could not watch the screenshot folder."); }
        }
    }

    private void QueueFile(string path)
    {
        lock (pending) { if (disposed || !pending.Add(path)) return; }
        _ = Task.Run(async () =>
        {
            try
            {
                for (var attempt = 0; attempt < 6; attempt++)
                {
                    await Task.Delay(250 * (attempt + 1), stop.Token);
                    try
                    {
                        await imageProcessing.WaitAsync(stop.Token);
                        try
                        {
                            var image = LoadImage(path, thumbnail: false);
                            await SaveImageAsync(image);
                        }
                        finally { imageProcessing.Release(); }
                        return;
                    }
                    catch (Exception ex) when (ex is IOException or NotSupportedException or COMException or FileFormatException)
                    {
                        if (attempt == 5 && File.Exists(path)) PublishFailure("Could not save the latest screenshot to the shelf.");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { PublishFailure("Could not save the latest screenshot to the shelf."); }
            finally { lock (pending) pending.Remove(path); }
        });
    }

    private async Task ReadClipboardAsync()
    {
        clipboardTimer.Stop();
        if (disposed || readingClipboard) return;
        var sequence = Native.GetClipboardSequenceNumber();
        var armedRevision = captureRevision;
        if (sequence == lastSequence || !CaptureOwnsClipboard()) return;
        readingClipboard = true;
        try
        {
            if (!Clipboard.ContainsImage()) return;
            var source = Clipboard.GetImage();
            if (source is null) return;
            if (sequence != Native.GetClipboardSequenceNumber() || !CaptureOwnsClipboard()) return;
            var image = new WriteableBitmap(source);
            image.Freeze();
            // Only mark this sequence consumed after its durable shelf copy succeeds.
            await imageProcessing.WaitAsync(stop.Token);
            try { await SaveImageAsync(image); }
            finally { imageProcessing.Release(); }
            lastSequence = sequence;
            if (armedRevision == captureRevision) captureUntil = DateTimeOffset.MinValue;
        }
        catch (ExternalException)
        {
            if (++clipboardAttempts < 6) clipboardTimer.Start();
        }
        catch (Exception) { PublishFailure("Could not save the screenshot from the capture tool."); }
        finally
        {
            readingClipboard = false;
            if (!disposed && Native.GetClipboardSequenceNumber() != sequence) clipboardTimer.Start();
        }
    }

    private bool CaptureOwnsClipboard()
    {
        try
        {
            Native.GetWindowThreadProcessId(Native.GetClipboardOwner(), out var pid);
            if (pid == 0) return DateTimeOffset.UtcNow <= captureUntil;
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            return IsCaptureProcess(name) ||
                (DateTimeOffset.UtcNow <= captureUntil &&
                    (name.Equals("explorer", StringComparison.OrdinalIgnoreCase) || name.Equals("RuntimeBroker", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    public static bool IsCaptureToolForeground()
    {
        try
        {
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var pid);
            if (pid == 0) return false;
            using var process = Process.GetProcessById((int)pid);
            return IsCaptureProcess(process.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private static bool IsCaptureProcess(string name) =>
        name.Equals("SnippingTool", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ScreenClippingHost", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ScreenSketch", StringComparison.OrdinalIgnoreCase);

    private Task SaveImageAsync(BitmapSource image) => Task.Run(async () =>
    {
        stop.Token.ThrowIfCancellationRequested();
        var pixels = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        pixels.Freeze();
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
        var fingerprint = Convert.ToHexString(digest.GetHashAndReset());
        using var buffer = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(pixels));
        encoder.Save(buffer);
        stop.Token.ThrowIfCancellationRequested();
        await shelf.AddScreenshotAsync(buffer, fingerprint);
        if (!disposed) await dispatcher.InvokeAsync(() => Changed?.Invoke());
    });

    public static BitmapSource LoadImage(string path, bool thumbnail, int decodeWidth = 192)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (thumbnail) image.DecodePixelWidth = Math.Clamp(decodeWidth, 64, 1600);
        image.StreamSource = input;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff";

    private void PublishFailure(string message)
    {
        if (!disposed) dispatcher.BeginInvoke(new Action(() => Failed?.Invoke(message)));
    }

    public void Dispose()
    {
        disposed = true;
        stop.Cancel();
        clipboardTimer.Stop();
        Native.RemoveClipboardFormatListener(window);
        foreach (var watcher in watchers) watcher.Dispose();
        watchers.Clear();
        // In-flight workers observe cancellation; the token source lives until they finish.
    }
}
