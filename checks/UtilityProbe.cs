using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterQuickSettings.Core;
using BetterQuickSettings.Interop;
using BetterQuickSettings.Services;
using BetterQuickSettings.UI;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BetterQuickSettings.Checks;

internal static class UtilityProbe
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    internal static async Task ClipboardAsync(string output)
    {
        Directory.CreateDirectory(output);
        var previous = SnapshotClipboard();
        var panel = new MainWindow(new LocalStore(Path.Combine(output, "isolated-data"))) { Title = "BetterQuickSettings clipboard validation" };
        ((CheckBox)panel.FindName("PinCheck")).IsChecked = true;
        try
        {
            var handle = new WindowInteropHelper(panel).EnsureHandle();
            await panel.Model.InitializeShelfAsync(handle, captureMode: false);
            await panel.Model.InitializeClipboardAsync(captureMode: false);
            var text = "Copied text\r\nTürkçe — 日本語 — 🌿\n" + new string('a', 320) + " searchable-tail";
            Clipboard.SetText(text);
            await Until(() => panel.Model.ClipboardItems.Count == 1);
            Clipboard.SetText("Second copied item");
            await Until(() => panel.Model.ClipboardItems.Count == 2);
            Clipboard.SetText(text);
            await Until(() => panel.Model.ClipboardItems[0].Entry.Text == text);
            var duplicate = panel.Model.ClipboardItems.Count == 2;
            panel.Model.ClipboardSearch = "searchable-tail";
            var fullTextSearch = panel.Model.ClipboardItems.Count == 1;
            var storedText = panel.Model.ClipboardItems[0].Entry.Text;
            await panel.Model.CopyClipboardItemAsync(panel.Model.ClipboardItems[0]);
            var returnedText = Clipboard.GetText();
            var copiedText = returnedText == text;
            var copySequence = panel.Model.ClipboardCopySequence;
            var readSequence = Native.GetClipboardSequenceNumber();
            panel.Model.ClipboardSearch = "";
            var repeatedTextCopies = true;
            for (var i = 0; i < 4; i++)
            {
                foreach (var expected in new[] { "Second copied item", text })
                {
                    await panel.Model.CopyClipboardItemAsync(panel.Model.ClipboardItems.Single(item => item.Entry.Text == expected));
                    repeatedTextCopies &= Clipboard.GetText() == expected;
                }
            }

            var image = FixtureImage();
            using (var png = Encode(image))
            {
                var data = new DataObject(); data.SetData("PNG", png, false); data.SetImage(image);
                Clipboard.SetDataObject(data, true);
            }
            await Until(() => panel.Model.ClipboardItems.Any(item => item.IsImage));
            var imageItem = panel.Model.ClipboardItems.Single(item => item.IsImage);
            await panel.Model.CopyClipboardItemAsync(imageItem);
            var pastedImage = Clipboard.GetImage();
            var copiedImage = pastedImage?.PixelWidth == image.PixelWidth && pastedImage.PixelHeight == image.PixelHeight &&
                Clipboard.GetData("PNG") is MemoryStream { Length: > 0 };
            var persisted = await new ClipboardHistoryStore(new LocalStore(Path.Combine(output, "isolated-data"))).LoadAsync();
            var restart = persisted.Count == 3 && persisted.Any(item => item.Text == text) &&
                persisted.Any(item => item.Kind == ClipboardItemKind.Image && File.Exists(panel.Model.ClipboardStore.ImagePath(item.Id)));
            var count = panel.Model.ClipboardItems.Count;
            panel.Model.ClipboardHistoryEnabled = false;
            Clipboard.SetText("This copy is made while history is paused");
            await Task.Delay(220);
            var paused = panel.Model.ClipboardItems.Count == count;
            panel.Model.ClipboardHistoryEnabled = true;
            await Task.Delay(80);
            var excluded = new DataObject(); excluded.SetText("Excluded clipboard fixture");
            excluded.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]), false);
            Clipboard.SetDataObject(excluded, true);
            await Task.Delay(220);
            var exclusions = panel.Model.ClipboardItems.Count == count;
            panel.Model.GlassAmount = 0;
            ((RadioButton)panel.FindName("ClipboardTab")).IsChecked = true;
            panel.OpenPanel(); await Task.Delay(150);
            panel.Capture(Path.Combine(output, "history.png"));
            Clipboard.SetText("Clear while a clipboard notification is pending");
            await panel.Model.ClearClipboardHistoryAsync();
            await Task.Delay(220);
            var cleared = panel.Model.ClipboardItems.Count == 0 && (await panel.Model.ClipboardStore.LoadAsync()).Count == 0;
            var separate = panel.Model.RecentScreenshots.Count == 0;
            WriteReport(output, new { Pass = duplicate && fullTextSearch && copiedText && repeatedTextCopies && copiedImage && restart && paused && exclusions && cleared && separate,
                Deduplicated = duplicate, FullTextSearch = fullTextSearch, TextPasteRoundTrip = copiedText, ImagePasteRoundTrip = copiedImage,
                RepeatedTextCopies = repeatedTextCopies,
                PersistedAcrossReload = restart, PausedCollection = paused, ExclusionRespected = exclusions, ClearStayedEmpty = cleared,
                OrdinaryCopiesStayedOutOfScreenshots = separate, panel.Model.Notice,
                TextComparison = new { ExpectedLength = text.Length, StoredLength = storedText?.Length, ReturnedLength = returnedText.Length,
                    CopySequence = copySequence, ReadSequence = readSequence } });
        }
        finally
        {
            panel.Cleanup(); panel.Close();
            if (previous is not null) Clipboard.SetDataObject(previous, true); else Clipboard.Clear();
        }
    }

    internal static async Task SendFileAsync(string directory)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var box = new Border { Background = Brushes.SlateBlue,
            Child = new TextBlock { Text = "Drag test file", Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var source = new Window { Title = "BetterQuickSettings external file source", Width = 240, Height = 130, Left = 180, Top = 180,
            WindowStyle = WindowStyle.None, Topmost = true, ShowInTaskbar = false, Content = box };
        var armed = false;
        var origin = new Point();
        box.MouseLeftButtonDown += (_, args) => { armed = true; origin = args.GetPosition(box); };
        box.MouseMove += (_, args) =>
        {
            if (!armed || args.LeftButton != MouseButtonState.Pressed || (args.GetPosition(box) - origin).Length < 5) return;
            armed = false;
            var data = new DataObject(DataFormats.FileDrop, new[] { Path.Combine(directory, "source.txt") });
            var effect = DragDrop.DoDragDrop(box, data, DragDropEffects.Copy);
            File.WriteAllText(Path.Combine(directory, "complete.txt"), effect.ToString());
            done.TrySetResult();
        };
        source.Show(); source.UpdateLayout();
        var point = box.PointToScreen(new Point(70, 50));
        File.WriteAllText(Path.Combine(directory, "ready.json"), JsonSerializer.Serialize(new { point.X, point.Y, Handle = new WindowInteropHelper(source).Handle.ToInt64() }));
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(25)); }
        finally { source.Close(); }
    }

    internal static async Task TaskbarDropAsync(string output)
    {
        Directory.CreateDirectory(output);
        Native.GetCursorPos(out var cursor);
        var panel = new MainWindow(new LocalStore(Path.Combine(output, "isolated-data"))) { Title = "BetterQuickSettings taskbar drop validation" };
        var hint = new DropHintWindow();
        using var integration = new ShellIntegration(false, Application.Current.Dispatcher);
        Process? source = null;
        try
        {
            await panel.Model.InitializeShelfAsync(new WindowInteropHelper(panel).EnsureHandle(), true);
            new WindowInteropHelper(hint).EnsureHandle();
            integration.DragTargetEntered += (x, y, bounds) => panel.Dispatcher.BeginInvoke(new Action(() => hint.OfferAt(x, y, bounds)));
            integration.DragReleased += () => panel.Dispatcher.BeginInvoke(new Action(hint.ScheduleHide));
            hint.FilesDropped += panel.ReceiveFiles;
            await Until(() => integration.TaskbarReady);
            var bounds = await Task.Run(() =>
            {
                var taskbar = AutomationElement.FromHandle(Native.FindWindow("Shell_TrayWnd", null));
                var targets = taskbar.FindAll(TreeScope.Descendants, new OrCondition(
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "ControlCenterButton"),
                    new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.AccentButton"),
                    new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.OmniButtonLeft"),
                    new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.OmniButtonRight")));
                return targets.Cast<AutomationElement>().Where(item => !item.Current.IsOffscreen)
                    .Select(item => item.Current.BoundingRectangle).Where(rect => rect.Width > 0).OrderBy(rect => rect.Left).First();
            });
            var destination = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            var beyondOldCorner = destination.X < Native.DisplayAt(new Native.Point { X = (int)destination.X, Y = (int)destination.Y }).Bounds.Right - 110;
            var senderDirectory = Path.Combine(output, "sender"); Directory.CreateDirectory(senderDirectory);
            var original = Path.Combine(senderDirectory, "source.txt");
            await File.WriteAllTextAsync(original, "An external file dropped on the closed panel's taskbar button.");
            source = Spawn("--drop-source", senderDirectory);
            await Until(() => File.Exists(Path.Combine(senderDirectory, "ready.json")));
            using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(senderDirectory, "ready.json")));
            var start = new Point(ready.RootElement.GetProperty("X").GetDouble(), ready.RootElement.GetProperty("Y").GetDouble());
            var sourceHandle = new IntPtr(ready.RootElement.GetProperty("Handle").GetInt64());
            Move(start); await Task.Delay(60); MouseDown();
            await MoveTo(start, destination);
            await Until(() => hint.IsVisible);
            var closedPanel = !panel.IsVisible;
            var noActivation = Native.GetForegroundWindow() == sourceHandle;
            var localPointer = hint.PointFromScreen(destination);
            var dropBounds = new Rect(0, 0, hint.ActualWidth, hint.ActualHeight);
            var coversButton = dropBounds.Contains(localPointer) && dropBounds.Contains(hint.PointFromScreen(new Point(bounds.Left + 1, bounds.Top + 1))) &&
                dropBounds.Contains(hint.PointFromScreen(new Point(bounds.Right - 1, bounds.Bottom - 1)));
            await Task.Delay(160);
            WindowCapture.Save(hint, Path.Combine(output, "taskbar-balloon.png"));
            var away = new Point(destination.X - 330, destination.Y - 170);
            await MoveTo(destination, away);
            await Until(() => !hint.IsVisible);
            await MoveTo(away, destination);
            await Until(() => hint.IsVisible);
            var returned = hint.IsVisible;
            Move(new Point(destination.X + 1, bounds.Bottom - 3)); await Task.Delay(80);
            Native.GetCursorPos(out var beforeRelease);
            var windowAtRelease = Native.GetAncestor(Native.WindowFromPoint(beforeRelease), 2);
            var hintAtRelease = windowAtRelease == new WindowInteropHelper(hint).Handle;
            var beforeDrop = hint.DragDiagnostic;
            MouseUp();
            try { await Until(() => panel.Model.HeldFiles.Count == 1); }
            catch
            {
                File.WriteAllText(Path.Combine(output, "drop-state.json"), JsonSerializer.Serialize(new
                { Target = bounds.ToString(), CoversButton = coversButton, Balloon = $"{hint.Left},{hint.Top},{hint.Width},{hint.Height}",
                    panel.Model.Notice, Foreground = Native.GetForegroundWindow().ToInt64(), HintAtRelease = hintAtRelease,
                    WindowAtRelease = windowAtRelease.ToInt64(), BeforeDrop = beforeDrop, AfterDrop = hint.DragDiagnostic }));
                throw;
            }
            await Until(() => !hint.IsVisible);
            var copy = panel.Model.HeldFiles.Single();
            var preserved = File.Exists(original) && copy.FullPath != original && File.ReadAllText(copy.FullPath) == File.ReadAllText(original);
            WriteReport(output, new { Pass = closedPanel && noActivation && coversButton && beyondOldCorner && returned && preserved && panel.IsVisible,
                OpenedWhilePanelClosed = closedPanel, DidNotStealFocus = noActivation, DirectReleaseOnButton = coversButton,
                OutsideOldCornerHotspot = beyondOldCorner,
                HintWasDropTarget = hintAtRelease, hint.DragDiagnostic,
                ReentryWorked = returned, OriginalAndShelfCopyPreserved = preserved, FilesOpenedAfterDrop = panel.IsVisible,
                BalloonDismissed = !hint.IsVisible, TaskbarBounds = bounds.ToString() });
        }
        finally
        {
            MouseUp(); Move(new Point(cursor.X, cursor.Y));
            hint.Close(); panel.Cleanup(); panel.Close();
            if (source is not null) { if (!source.HasExited) source.Kill(); source.Dispose(); }
        }
    }

    internal static async Task VolumeAsync(string output)
    {
        Directory.CreateDirectory(output);
        Native.GetCursorPos(out var cursor);
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var oldVolume = device.AudioEndpointVolume.MasterVolumeLevelScalar;
        var oldMute = device.AudioEndpointVolume.Mute;
        var panel = new MainWindow(new LocalStore(Path.Combine(output, "isolated-data"))) { Title = "BetterQuickSettings volume feedback validation" };
        ((CheckBox)panel.FindName("PinCheck")).IsChecked = true;
        using var loopback = new WasapiLoopbackCapture(device) { WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2) };
        var levels = new List<(long Time, double Peak)>();
        loopback.DataAvailable += (_, args) =>
        {
            var floats = MemoryMarshal.Cast<byte, float>(args.Buffer.AsSpan(0, args.BytesRecorded));
            double peak = 0;
            foreach (var value in floats) peak = Math.Max(peak, Math.Abs(value));
            lock (levels) levels.Add((Stopwatch.GetTimestamp(), peak));
        };
        try
        {
            await panel.Model.InitializeAsync(Native.MonitorFromWindow(new WindowInteropHelper(panel).EnsureHandle(), 2));
            device.AudioEndpointVolume.Mute = false;
            panel.Model.Volume = 30;
            panel.OpenPanel(); await Task.Delay(220);
            var slider = (Slider)panel.FindName("VolumeSlider");
            var silentProgrammatic = panel.Model.VolumeFeedbackCount == 0;
            loopback.StartRecording();
            var start = slider.PointToScreen(new Point(slider.ActualWidth * 0.28, slider.ActualHeight / 2));
            var finish = slider.PointToScreen(new Point(slider.ActualWidth * 0.40, slider.ActualHeight / 2));
            Move(start); await Task.Delay(50); MouseDown();
            await Until(() => panel.Model.VolumeFeedbackReady);
            await MoveTo(start, finish);
            var silentDuringDrag = panel.Model.VolumeFeedbackCount == 0;
            var released = Stopwatch.GetTimestamp(); MouseUp();
            await Until(() => panel.Model.VolumeFeedbackCount == 1);
            await Task.Delay(230);
            for (var i = 0; i < 5; i++)
            {
                Move(slider.PointToScreen(new Point(slider.ActualWidth * (0.34 + i * 0.015), slider.ActualHeight / 2)));
                MouseDown(); await Task.Delay(24); MouseUp(); await Task.Delay(48);
            }
            var lastRelease = Stopwatch.GetTimestamp();
            await Task.Delay(180);
            var sounds = panel.Model.VolumeFeedbackCount;
            await Until(() => !panel.Model.VolumeFeedbackReady);
            loopback.StopRecording();
            (long Time, double Peak)[] measured;
            lock (levels) measured = levels.ToArray();
            var beforePeak = measured.Where(sample => sample.Time < released).Select(sample => sample.Peak).DefaultIfEmpty().Max();
            var afterPeak = measured.Where(sample => sample.Time >= released).Select(sample => sample.Peak).DefaultIfEmpty().Max();
            var audible = measured.Where(sample => sample.Time >= released && sample.Peak > Math.Max(0.001, beforePeak * 1.5)).ToArray();
            var latency = audible.Length == 0 ? -1 : (audible[0].Time - released) * 1000.0 / Stopwatch.Frequency;
            var tail = audible.Length == 0 ? -1 : (audible[^1].Time - lastRelease) * 1000.0 / Stopwatch.Frequency;
            var played = audible.Length > 0;
            WriteReport(output, new { Pass = silentProgrammatic && silentDuringDrag && sounds == 6 && played && !panel.Model.VolumeFeedbackReady,
                ProgrammaticChangesSilent = silentProgrammatic, NoToneWhileDragging = silentDuringDrag,
                Releases = 6, SoundsStarted = sounds, LoopbackDetected = played, BeforePeak = beforePeak, AfterPeak = afterPeak,
                FirstLoopbackCallbackMs = Math.Round(latency, 2), TailAfterBurstMs = Math.Round(tail, 2), IdleAudioReleased = !panel.Model.VolumeFeedbackReady });
        }
        finally
        {
            MouseUp(); Move(new Point(cursor.X, cursor.Y));
            loopback.StopRecording(); panel.Cleanup(); panel.Close();
            device.AudioEndpointVolume.MasterVolumeLevelScalar = oldVolume;
            device.AudioEndpointVolume.Mute = oldMute;
        }
    }

    private static DataObject? SnapshotClipboard()
    {
        var previous = Clipboard.GetDataObject();
        if (previous is null) return null;
        var snapshot = new DataObject();
        foreach (var format in new[] { DataFormats.UnicodeText, DataFormats.Text, DataFormats.Rtf, DataFormats.Html,
            DataFormats.FileDrop, "PNG", DataFormats.Bitmap, "CanIncludeInClipboardHistory", "ExcludeClipboardContentFromMonitorProcessing" })
        {
            if (!previous.GetDataPresent(format, false)) continue;
            var value = previous.GetData(format, false);
            if (value is MemoryStream stream) value = new MemoryStream(stream.ToArray());
            if (value is BitmapSource bitmap) { var image = new WriteableBitmap(bitmap); image.Freeze(); value = image; }
            if (value is not null) snapshot.SetData(format, value, false);
        }
        return snapshot;
    }
    private static BitmapSource FixtureImage()
    {
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen()) dc.DrawRoundedRectangle(new LinearGradientBrush(Colors.CornflowerBlue, Colors.Teal, 25), null, new Rect(4, 4, 112, 72), 10, 10);
        var image = new RenderTargetBitmap(120, 80, 96, 96, PixelFormats.Pbgra32); image.Render(drawing); image.Freeze(); return image;
    }
    private static MemoryStream Encode(BitmapSource image)
    {
        var png = new MemoryStream(); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(png); png.Position = 0; return png;
    }
    private static Process Spawn(string command, string directory)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Process path is unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add(command); start.ArgumentList.Add(directory); return Process.Start(start)!;
    }
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 120; i++) { if (condition()) return; await Task.Delay(50); }
        throw new TimeoutException("The native utility check did not reach its expected state.");
    }
    private static async Task MoveTo(Point from, Point to)
    {
        for (var i = 1; i <= 18; i++) { Move(from + (to - from) * (i / 18.0)); await Task.Delay(25); }
    }
    private static void Move(Point point) => SetCursorPos((int)point.X, (int)point.Y);
    private static void MouseDown() => mouse_event(2, 0, 0, 0, UIntPtr.Zero);
    private static void MouseUp() => mouse_event(4, 0, 0, 0, UIntPtr.Zero);
    private static void WriteReport(string output, object report)
    {
        var text = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, "report.json"), text);
        using var document = JsonDocument.Parse(text);
        if (!document.RootElement.GetProperty("Pass").GetBoolean()) throw new InvalidOperationException("Utility validation failed; inspect report.json.");
    }
}
