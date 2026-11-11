using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using BetterQuickSettings.Core;
using BetterQuickSettings.Interop;
using BetterQuickSettings.Services;
using BetterQuickSettings.UI;

namespace BetterQuickSettings;

public partial class MainWindow : Window
{
    public PanelViewModel Model { get; }
    private readonly bool captureMode;
    private bool positioning;
    private bool initialized;
    private IntPtr lastMonitor;
    private DateTime lastDismissed;
    private Point fileDragStart;
    private FrameworkElement? fileDragSource;
    private bool startingFileDrag;
    private FrameworkElement? currentPage;
    private bool sizeQueued;
    private bool toolsSizeQueued;
    private readonly PanelLayout layout;
    private readonly PageTransition pageTransition;
    private double resizeStartHeight, resizeBottom, resizeScale;
    private int resizeStartY;
    private FileDragSession? dragSession;
    private string lastDragDiagnostic = "not started";
    private bool reopenAfterDrag;
    internal int StartedDragCount { get; private set; }
    internal string DragDiagnostic => dragSession?.Diagnostic ?? lastDragDiagnostic;
    private Native.Display anchorDisplay;
    private bool anchorReady;
    public bool IsFileDragInProgress => dragSession is { Completed: false };
    internal double VisibleSurfaceHeight => PanelSurface.ActualHeight;
    internal double SurfaceOffset => (PanelContent.RenderTransform as TranslateTransform)?.Y ?? 0;
    internal string TransitionState => $"fixed shell={ActualHeight:0.##}; surface={PanelSurface.ActualHeight:0.##}; page={currentPage?.ActualHeight:0.##}";
    // Retained protocol name for interoperability with pre-release shelf drags.
    private const string ShelfDragFormat = "Ada.FileShelf.Origin";
    public const string WindowTitle = "BetterQuickSettings";
    internal const string LegacyWindowTitle = "Ada · Control Center";
    public const uint ToggleMessage = 0x8001;
    private static readonly uint TaskbarCreatedMessage = Native.RegisterWindowMessage("TaskbarCreated");
    public event Action? ShellLayoutChanged;

    public MainWindow(LocalStore store, bool captureMode = false)
    {
        this.captureMode = captureMode;
        Model = new PanelViewModel(store, Dispatcher);
        InitializeComponent();
        layout = new PanelLayout(this, PanelSurface, PanelContent, PositionPanel, () => Model.PanelHeight);
        pageTransition = new PageTransition(PageHost);
        UiMotion.SetSuppress(this, captureMode);
        currentPage = ControlsPage;
        DataContext = Model;
        VolumeSlider.AddHandler(UiMotion.ValueInteractionStartedEvent, new RoutedEventHandler((_, _) => Model.BeginVolumeFeedback()));
        VolumeSlider.AddHandler(UiMotion.ValueCommittedEvent, new RoutedEventHandler((_, _) => Model.CommitVolume()));
        VolumeSlider.AddHandler(UiMotion.ValueInteractionCancelledEvent, new RoutedEventHandler((_, _) => Model.CancelVolumeFeedback()));
        Model.AppearanceChanged += UpdateAppearance;
        Model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(PanelViewModel.Media) or nameof(PanelViewModel.HasScreenshots) or
                nameof(PanelViewModel.HasHeldFiles) or nameof(PanelViewModel.HasNotes) or nameof(PanelViewModel.IsEditingNote) or
                nameof(PanelViewModel.FileCount) or nameof(PanelViewModel.HasNotice) ||
                (args.PropertyName == nameof(PanelViewModel.Note) && currentPage == NotesPage && Model.IsComposingNote))
                QueueSize();
        };
        Model.Outputs.CollectionChanged += (_, _) => { if (OutputPicker.Visibility == Visibility.Visible) QueueSize(); };
        StartupCheck.IsChecked = SystemActions.StartsWithWindows();
        SizeChanged += (_, _) => { if (layout?.IsResizing != true) PositionPanel(); };
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
            UpdateAppearance();
            PositionPanel();
        };
        Loaded += (_, _) => UpdateAppearance();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
    }

    public async Task InitializeServicesAsync()
    {
        lastMonitor = Native.MonitorFromWindow(new WindowInteropHelper(this).EnsureHandle(), 2);
        await Task.WhenAll(Model.InitializeAsync(lastMonitor),
            Model.InitializeShelfAsync(new WindowInteropHelper(this).Handle, captureMode), Model.InitializeClipboardAsync(captureMode));
        initialized = true;
    }

    private void DisplaySettingsChanged(object? sender, EventArgs args) => Dispatcher.BeginInvoke(new Action(() =>
    {
        anchorDisplay = Native.DisplayAtCursor();
        PositionPanel();
        layout.RefreshSize();
        ShellLayoutChanged?.Invoke();
    }));
    private IntPtr WindowMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == ToggleMessage) { TogglePanel(); handled = true; }
        if ((uint)message == TaskbarCreatedMessage || message == 0x007E)
            ShellLayoutChanged?.Invoke();
        if (message == 0x031D) Model.ClipboardChanged();
        if (message == 0x02E0) Dispatcher.BeginInvoke(new Action(() => { PositionPanel(); layout.RefreshSize(); }));
        if (message is 0x031E or 0x001A or 0x031A or 0x0218)
            Dispatcher.BeginInvoke(new Action(() => GlassBackdrop.Apply(this, captureMode, force: true, amount: Model.GlassAmount)));
        if (message == 0x001A && wParam.ToInt32() == 0x002F) ShellLayoutChanged?.Invoke();
        return IntPtr.Zero;
    }

    public void PositionPanel()
    {
        if (positioning || layout?.IsResizing == true) return;
        positioning = true;
        try
        {
            if (!anchorReady) { anchorDisplay = Native.DisplayAtCursor(); anchorReady = true; }
            var screen = anchorDisplay;
            var area = screen.WorkingArea;
            var source = PresentationSource.FromVisual(this);
            var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var corner = fromDevice.Transform(new Point(area.Right, area.Bottom));
            var size = fromDevice.Transform(new Vector(area.Width, area.Height));
            var maximum = Math.Max(180, size.Y - 20);
            MinHeight = Math.Min(PanelLayout.MinimumHeight, maximum);
            MaxHeight = maximum;
            Left = corner.X - Width - 10;
            Top = corner.Y - Height - 10;
        }
        finally { positioning = false; }
    }

    public async void TogglePanel()
    {
        if (dragSession is { Completed: false })
        {
            reopenAfterDrag = true;
            dragSession.Cancel();
            return;
        }
        if (IsVisible) { Dismiss(); return; }
        // Clicking the tray deactivates the old window before the mouse-up callback arrives.
        if ((DateTime.UtcNow - lastDismissed).TotalMilliseconds < 180) return;
        OpenPanel();
        if (initialized)
        {
            lastMonitor = Native.MonitorFromWindow(new WindowInteropHelper(this).Handle, 2);
            await Model.RefreshEnvironmentAsync(lastMonitor);
        }
    }

    public void OpenPanel()
    {
        var animate = !captureMode && UiMotion.Enabled;
        pageTransition.Finish(currentPage);
        layout.Finish();
        anchorDisplay = Native.DisplayAtCursor(); anchorReady = true;
        PositionPanel();
        layout.RefreshSize();
        if (!captureMode) { GlassBackdrop.PrepareBackground(this); UpdateAppearance(); }
        PanelContent.Opacity = animate ? 0.8 : 1;
        Show(); UpdateLayout(); layout.RefreshSize(); PositionPanel();
        Native.ActivatePanel(new WindowInteropHelper(this).Handle);
        Activate();
        if (NotesPage.Visibility == Visibility.Visible) FocusNoteInput(); else Focus();
        if (animate) UiMotion.Reveal(PanelContent, 120, 3);
    }

    private void UpdateAppearance() => GlassBackdrop.Apply(this, captureMode, amount: Model.GlassAmount);

    private void QueueSize()
    {
        if (sizeQueued) return;
        sizeQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            sizeQueued = false;
            if (IsLoaded) layout.RefreshSize();
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    public void Dismiss()
    {
        if (PanelResizeGrip.IsDragging) PanelResizeGrip.CancelDrag();
        pageTransition.Finish(currentPage);
        layout.Finish();
        PanelSurface.RenderTransform = Transform.Identity;
        PanelSurface.BeginAnimation(OpacityProperty, null);
        PanelSurface.Opacity = 1;
        PanelContent.BeginAnimation(OpacityProperty, null);
        PanelContent.Opacity = 1;
        PanelContent.RenderTransform = Transform.Identity;
        var armedSource = fileDragSource;
        fileDragSource = null;
        armedSource?.ReleaseMouseCapture();
        Model.FileDragOver = false;
        Model.CancelVolumeFeedback();
        Model.SaveNote();
        Hide();
        lastDismissed = DateTime.UtcNow;
    }

    private void OnDeactivated(object? sender, EventArgs args)
    {
        if (!captureMode && !IsFileDragInProgress && PinCheck.IsChecked != true) Dismiss();
    }
    private void OnKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape) { dragSession?.Cancel(); Dismiss(); args.Handled = true; }
        if (args.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        { NotesTab.IsChecked = true; NotesBackClick(this, new RoutedEventArgs()); args.Handled = true; }
        if (args.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && NotesPage.Visibility == Visibility.Visible)
        { AddNoteClick(this, new RoutedEventArgs()); args.Handled = true; }
        if (args.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (ClipboardPage.Visibility == Visibility.Visible) ClipboardSearchBox.Focus(); else FilesTab.IsChecked = true;
            args.Handled = true;
        }
        if (args.Key == Key.H && Keyboard.Modifiers == ModifierKeys.Control)
        { ClipboardTab.IsChecked = true; args.Handled = true; }
    }
    private void DismissClick(object sender, RoutedEventArgs args) => Dismiss();
    private void QuitClick(object sender, RoutedEventArgs args) => Application.Current.Shutdown();
    private void ControlsChecked(object sender, RoutedEventArgs args)
    {
        if (ControlsPage is null) return;
        ShowPage(ControlsPage);
    }
    private void NotesChecked(object sender, RoutedEventArgs args)
    {
        if (NotesPage is null) return;
        ShowPage(NotesPage);
        Dispatcher.BeginInvoke(new Action(FocusNoteInput));
    }
    private void PreferencesClick(object sender, RoutedEventArgs args)
    {
        ControlsTab.IsChecked = NotesTab.IsChecked = FilesTab.IsChecked = ClipboardTab.IsChecked = false;
        ShowPage(PreferencesPage);
    }
    private void ShowPage(FrameworkElement page)
    {
        if (PageHost is null || ReferenceEquals(currentPage, page)) return;
        if (currentPage?.IsKeyboardFocusWithin == true) Focus();
        pageTransition.Show(currentPage, page, !captureMode && IsVisible && UiMotion.Enabled && !layout.IsResizing);
        currentPage = page;
    }
    private void OutputClick(object sender, RoutedEventArgs args)
    {
        layout.Change(() => OutputPicker.Visibility = OutputPicker.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible);
        if (OutputPicker.Visibility == Visibility.Visible) UiMotion.Reveal(OutputPicker);
    }

    private void ToolsChanged(object sender, RoutedEventArgs args)
    {
        if (layout is null || !IsLoaded || toolsSizeQueued) return;
        toolsSizeQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            toolsSizeQueued = false;
            layout.Change(() =>
            {
                ToolsExpander.ApplyTemplate();
                if (ToolsExpander.Template.FindName("ToolsViewport", ToolsExpander) is Border viewport)
                    viewport.Height = ToolsExpander.IsExpanded ? double.NaN : 0;
                ToolsExpander.InvalidateMeasure();
                ToolsExpander.UpdateLayout();
            });
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void FilesChecked(object sender, RoutedEventArgs args)
    {
        if (FilesPage is null) return;
        ShowPage(FilesPage);
    }

    private void OpenFilesClick(object sender, RoutedEventArgs args) => FilesTab.IsChecked = true;

    private void PanelResizeStarted(object sender, DragStartedEventArgs args)
    {
        if (IsFileDragInProgress || !Native.GetCursorPos(out var pointer)) { PanelResizeGrip.CancelDrag(); return; }
        resizeStartY = pointer.Y;
        resizeStartHeight = Height;
        resizeBottom = Top + Height;
        resizeScale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        layout.IsResizing = true;
        pageTransition.Finish(currentPage);
        args.Handled = true;
    }

    private void PanelResizeDelta(object sender, DragDeltaEventArgs args)
    {
        if (!layout.IsResizing || !Native.GetCursorPos(out var pointer)) return;
        // Use screen coordinates: moving the top edge must not feed its motion back into the drag delta.
        var height = Math.Clamp(resizeStartHeight - (pointer.Y - resizeStartY) / resizeScale, MinHeight, MaxHeight);
        Height = height;
        Top = resizeBottom - height;
        args.Handled = true;
    }

    private void PanelResizeCompleted(object sender, DragCompletedEventArgs args)
    {
        if (!layout.IsResizing) return;
        if (args.Canceled) { Height = resizeStartHeight; Top = resizeBottom - Height; }
        layout.IsResizing = false;
        Model.PanelHeight = Height;
        PositionPanel();
        layout.RefreshSize();
        args.Handled = true;
    }

    private void ScreenshotPreviewSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is FrameworkElement element)
            element.Clip = new RectangleGeometry(new Rect(args.NewSize), 11, 11);
    }

    private void ClipboardChecked(object sender, RoutedEventArgs args)
    {
        if (ClipboardPage is null) return;
        ShowPage(ClipboardPage);
    }
    private async void CopyClipboardClick(object sender, RoutedEventArgs args)
    { if (((FrameworkElement)sender).DataContext is ClipboardItemViewModel item) await Model.CopyClipboardItemAsync(item); }
    private async void RemoveClipboardClick(object sender, RoutedEventArgs args)
    { if (((FrameworkElement)sender).DataContext is ClipboardItemViewModel item) await Model.RemoveClipboardItemAsync(item); }
    private async void ClearClipboardClick(object sender, RoutedEventArgs args) => await Model.ClearClipboardHistoryAsync();
    private async void PreviewClipboardImageClick(object sender, RoutedEventArgs args)
    {
        if (((FrameworkElement)sender).DataContext is not ClipboardItemViewModel { IsImage: true } item) return;
        await Model.TryAsync(async () =>
        {
            Dismiss();
            var preview = new ImagePreviewWindow();
            preview.Show();
            await preview.LoadAsync(Model.ClipboardStore.ImagePath(item.Entry.Id));
        });
    }

    public async void ReceiveFiles(string[] paths)
    {
        FilesTab.IsChecked = true;
        if (!IsVisible) OpenPanel();
        await Model.AddFilesAsync(paths);
    }

    public static bool AcceptsFiles(DragEventArgs args)
    {
        try
        {
            return (args.AllowedEffects & DragDropEffects.Copy) != 0 &&
                !args.Data.GetDataPresent(ShelfDragFormat) && args.Data.GetDataPresent(DataFormats.FileDrop);
        }
        catch (System.Runtime.InteropServices.ExternalException) { return false; }
    }

    private void OnFilesDragOver(object sender, DragEventArgs args)
    {
        if (!AcceptsFiles(args)) return;
        Model.FileDragOver = true;
        args.Effects = DragDropEffects.Copy;
        args.Handled = true;
    }

    private void OnFilesDragLeave(object sender, DragEventArgs args) => Model.FileDragOver = false;

    private void OnFilesDrop(object sender, DragEventArgs args)
    {
        Model.FileDragOver = false;
        if (!AcceptsFiles(args)) return;
        try
        {
            if (args.Data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                args.Effects = DragDropEffects.Copy;
                args.Handled = true;
                ReceiveFiles(paths);
            }
        }
        catch (Exception ex) { args.Effects = DragDropEffects.None; Model.Notice = "Could not read the dropped files: " + ex.Message; }
    }

    private void FileMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (startingFileDrag || IsFileDragInProgress) return;
        if (sender is not FrameworkElement element || element.DataContext is not ShelfItemViewModel) return;
        for (var node = args.OriginalSource as DependencyObject; node is not null && node != element;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is ButtonBase) return;
        fileDragStart = args.GetPosition(this);
        fileDragSource = element;
        element.Focus();
        element.CaptureMouse();
    }

    private void FileMouseUp(object sender, MouseButtonEventArgs args)
    {
        var source = fileDragSource;
        fileDragSource = null;
        source?.ReleaseMouseCapture();
        if (source?.DataContext is ShelfItemViewModel item)
        {
            var point = args.GetPosition(source);
            if (point.X >= 0 && point.Y >= 0 && point.X <= source.ActualWidth && point.Y <= source.ActualHeight &&
                (item.Thumbnail is not null || item.IsScreenshot || args.ClickCount == 2))
                OpenFile(item);
        }
    }

    private void FileMouseMove(object sender, MouseEventArgs args)
    {
        if (startingFileDrag || fileDragSource is null || args.LeftButton != MouseButtonState.Pressed ||
            fileDragSource.DataContext is not ShelfItemViewModel item) return;
        var point = args.GetPosition(this);
        if (Math.Abs(point.X - fileDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - fileDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var source = fileDragSource;
        // Releasing WPF capture can synchronously re-enter mouse handlers.
        // Disarm the source before release, and keep creation guarded until the session exists.
        fileDragSource = null;
        startingFileDrag = true;
        source.ReleaseMouseCapture();
        args.Handled = true;
        if (dragSession is { Completed: false })
        {
            dragSession.Cancel();
            Model.Notice = "The previous drop is still being handled by the receiving app.";
            startingFileDrag = false;
            return;
        }
        FileDragSession? session = null;
        try
        {
            FileDropData(item);
            session = new FileDragSession(Dispatcher, item.FullPath, item.Entry.Id.ToString(), item.IsScreenshot,
                item.Thumbnail, VisualTreeHelper.GetDpi(this).DpiScaleX);
            StartedDragCount++;
            dragSession = session;
            startingFileDrag = false;
            session.PointerFinished += () =>
            {
                Model.FileDragOver = false;
            };
            session.Run(source);
        }
        catch (Exception ex) { Model.Notice = "Could not drag the file: " + ex.Message; }
        finally
        {
            startingFileDrag = false;
            session?.Dispose();
            if (session is not null) lastDragDiagnostic = session.Diagnostic;
            if (ReferenceEquals(dragSession, session)) dragSession = null;
            if (reopenAfterDrag)
            {
                reopenAfterDrag = false;
                Dispatcher.BeginInvoke(new Action(OpenPanel));
            }
            else if (!IsActive && PinCheck.IsChecked != true) Dismiss();
        }
    }

    private static DataObject FileDropData(ShelfItemViewModel item)
    {
        if (!File.Exists(item.FullPath) && !Directory.Exists(item.FullPath))
            throw new FileNotFoundException("This shelf copy is no longer available.");
        var files = new StringCollection { item.FullPath };
        var data = new DataObject();
        data.SetFileDropList(files);
        data.SetData(ShelfDragFormat, item.Entry.Id.ToString());
        return data;
    }

    private void FileKeyDown(object sender, KeyEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ShelfItemViewModel item }) return;
        if (args.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        { Model.Try(() => Clipboard.SetDataObject(FileDropData(item), true)); args.Handled = true; }
        else if (args.Key == Key.Enter)
        { OpenFile(item); args.Handled = true; }
    }

    private async void OpenFile(ShelfItemViewModel item)
    {
        await Model.TryAsync(async () =>
        {
            FileDropData(item);
            if (item.IsScreenshot || ScreenshotService.IsImage(item.FullPath))
            {
                Dismiss();
                var preview = new ImagePreviewWindow();
                preview.Show();
                await preview.LoadAsync(item.FullPath);
            }
            else Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
        });
    }

    private void OpenFileClick(object sender, RoutedEventArgs args)
    { if (((FrameworkElement)sender).DataContext is ShelfItemViewModel item) OpenFile(item); }
    private void CopyFileClick(object sender, RoutedEventArgs args)
    { if (((FrameworkElement)sender).DataContext is ShelfItemViewModel item) Model.Try(() => Clipboard.SetDataObject(FileDropData(item), true)); }
    private async void RemoveFileClick(object sender, RoutedEventArgs args)
    { if (((FrameworkElement)sender).DataContext is ShelfItemViewModel item) await Model.RemoveFileAsync(item); }
    private async void KeepScreenshotClick(object sender, RoutedEventArgs args)
    { if (((FrameworkElement)sender).DataContext is ShelfItemViewModel item) await Model.KeepScreenshotAsync(item); }

    private void CaptureClick(object sender, RoutedEventArgs args)
    {
        Model.ArmScreenshot();
        Dismiss();
        Model.Try(Native.StartSnip);
    }

    private void ScreenshotFolderClick(object sender, RoutedEventArgs args)
    {
        var pinned = PinCheck.IsChecked;
        PinCheck.IsChecked = true;
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a screenshot folder" };
            if (dialog.ShowDialog(this) == true) Model.SetScreenshotFolder(dialog.FolderName);
        }
        finally { PinCheck.IsChecked = pinned; }
    }
    private void SelectOutputClick(object sender, RoutedEventArgs args)
    {
        if (((Button)sender).Tag is AudioOutput output) Model.SelectOutput(output);
    }
    private void PreferHeadphonesClick(object sender, RoutedEventArgs args)
    {
        if (((Button)sender).Tag is AudioOutput output) Model.PreferHeadphones(output);
    }
    private void MuteClick(object sender, RoutedEventArgs args) => Model.ToggleMute();
    private async void PlayClick(object sender, RoutedEventArgs args) => await Model.PlayPauseAsync();
    private async void PreviousClick(object sender, RoutedEventArgs args) => await Model.PreviousAsync();
    private async void NextClick(object sender, RoutedEventArgs args) => await Model.NextAsync();
    private void FocusClick(object sender, RoutedEventArgs args) => Model.ToggleFocus();
    private void ClearNoticeClick(object sender, RoutedEventArgs args) => Model.Notice = "";
    private void SettingsClick(object sender, RoutedEventArgs args)
    {
        var page = ((Button)sender).Tag as string ?? "";
        Model.Try(() => { SystemActions.OpenSettings(page); Dismiss(); });
    }
    private void CastClick(object sender, RoutedEventArgs args)
    {
        Model.Try(() => { Dismiss(); Native.WindowsShortcut(0x4B); });
    }
    private void StartupClick(object sender, RoutedEventArgs args)
    {
        Model.Try(() => SystemActions.SetStartWithWindows(StartupCheck.IsChecked == true));
        StartupCheck.IsChecked = SystemActions.StartsWithWindows();
    }
    private void CopyNoteClick(object sender, RoutedEventArgs args)
    {
        if (Model.Note.Length == 0) return;
        Model.Try(() => Clipboard.SetText(Model.Note));
    }
    private void FocusNoteInput()
    {
        if (Model.IsEditingNote) NoteEditor.Focus(); else NoteComposer.Focus();
    }
    private void AddNoteClick(object sender, RoutedEventArgs args)
    {
        layout.Change(() => { if (Model.IsEditingNote) Model.ShowNotesList(); else Model.AddNote(); });
        FocusNoteInput();
    }
    private void EditNoteClick(object sender, RoutedEventArgs args)
    {
        if (((FrameworkElement)sender).DataContext is not QuickNote note) return;
        layout.Change(() => Model.EditNote(note));
        FocusNoteInput();
    }
    private void NotesBackClick(object sender, RoutedEventArgs args)
    {
        layout.Change(() => Model.ShowNotesList());
        FocusNoteInput();
    }
    private void DeleteNoteClick(object sender, RoutedEventArgs args)
    {
        layout.Change(() =>
        {
            if (((FrameworkElement)sender).DataContext is QuickNote note) Model.DeleteNote(note.Id);
            else Model.DeleteEditingNote();
        });
        FocusNoteInput();
    }

    public void Capture(string path)
    {
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public void ShowNotesForCapture() { NotesTab.IsChecked = true; UpdateLayout(); }
    public void ShowPreferencesForCapture() { PreferencesClick(this, new RoutedEventArgs()); UpdateLayout(); }
    public void Cleanup()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        dragSession?.Dispose();
        Model.Dispose();
    }
}
