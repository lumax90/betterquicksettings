using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterQuickSettings.Core;
using BetterQuickSettings.Services;

namespace BetterQuickSettings.UI;

public sealed class PanelViewModel : Observable, IDisposable
{
    private readonly LocalStore store;
    private readonly Dispatcher dispatcher;
    private readonly BrightnessService brightnessService = new();
    private readonly DispatcherTimer noteTimer, brightnessTimer, focusTimer, volumeTimer, preferencesTimer, appearanceTimer, copiedTimer;
    private bool volumePending, brightnessPending, brightnessWriting, preferencesDirty;
    public FileShelfStore ShelfStore { get; }
    public ClipboardHistoryStore ClipboardStore { get; }
    private ScreenshotService? screenshots;
    private ClipboardHistoryService? clipboard;
    private readonly VolumeFeedbackService volumeFeedback = new();
    private IReadOnlyList<ClipboardItemViewModel> clipboardItems = Array.Empty<ClipboardItemViewModel>();
    private string clipboardSearch = "";
    private int clipboardRefreshRevision;
    private Guid? copiedClipboardId;
    private int shelfRefreshRevision;
    private Guid? latestPreviewId;
    private BitmapSource? latestScreenshotPreview;
    private bool disposed;
    private bool addingFiles;
    private bool fileDragOver;
    private readonly SemaphoreSlim fileImports = new(1, 1);
    private int pendingFileBatches;
    private AudioService? audio;
    private IReadOnlyList<AudioOutput>? displayedOutputs;
    private MediaService? media;
    private bool syncing;
    private Preferences preferences = new();
    private double volume, brightness;
    private bool muted, audioAvailable, brightnessAvailable;
    private string outputName = "Finding audio devices…", brightnessDescription = "Checking display…";
    private string note = "", noteStatus = "", notice = "", network = "Network settings", bluetooth = "Device settings";
    private string integrationStatus = "Connecting…";
    private MediaState mediaState = MediaState.Empty;
    private DateTimeOffset? focusEnds;
    private string focusLabel = "25 min";
    private bool noteDirty;
    private NotesDocument notebook = new();
    private QuickNote? editingNote;
    private bool notesReady;
    private int brightnessRevision;

    public ObservableCollection<AudioOutput> Outputs { get; } = new();
    public ObservableCollection<QuickNote> Notes { get; } = new();
    public ObservableCollection<ClipboardItemViewModel> ClipboardItems { get; } = new();
    public bool HasClipboardItems => clipboardItems.Count > 0;
    public bool HasClipboardResults => ClipboardItems.Count > 0;
    public string ClipboardSummary => (ClipboardHistoryEnabled ? "" : "Paused · ") + $"{clipboardItems.Count} / {ClipboardHistoryStore.ItemLimit}";
    public string ClipboardEmptyMessage => ClipboardSearch.Length > 0 ? "No matches" :
        ClipboardHistoryEnabled ? "Copied text and images appear here" : "Clipboard history is paused";
    public string ClipboardSearch
    {
        get => clipboardSearch;
        set { if (Set(ref clipboardSearch, value)) FilterClipboard(); }
    }
    public bool ClipboardHistoryEnabled
    {
        get => preferences.ClipboardHistoryEnabled;
        set
        {
            if (value == preferences.ClipboardHistoryEnabled) return;
            preferences = preferences with { ClipboardHistoryEnabled = value };
            if (clipboard is not null) clipboard.Enabled = value;
            SavePreferences(); Changed(); Changed(nameof(ClipboardSummary)); Changed(nameof(ClipboardEmptyMessage));
        }
    }
    public bool VolumeFeedbackEnabled
    {
        get => preferences.VolumeFeedbackEnabled;
        set
        {
            if (value == preferences.VolumeFeedbackEnabled) return;
            preferences = preferences with { VolumeFeedbackEnabled = value };
            if (!value) volumeFeedback.Cancel();
            SavePreferences(); Changed();
        }
    }
    public bool HasNotes => Notes.Count > 0;
    public bool IsEditingNote => editingNote is not null;
    public bool IsComposingNote => editingNote is null;
    public bool NoteReadOnly => !notesReady;
    public bool CanAddNote => notesReady && !IsEditingNote && !string.IsNullOrWhiteSpace(Note);
    public string EditingNoteLabel => editingNote?.ModifiedLabel ?? "";
    public ObservableCollection<ShelfItemViewModel> RecentScreenshots { get; } = new();
    public ObservableCollection<ShelfItemViewModel> HeldFiles { get; } = new();
    public bool HasScreenshots => RecentScreenshots.Count > 0;
    public bool HasHeldFiles => HeldFiles.Count > 0;
    public ShelfItemViewModel? LatestScreenshot => RecentScreenshots.FirstOrDefault();
    public BitmapSource? LatestScreenshotPreview { get => latestScreenshotPreview; private set => Set(ref latestScreenshotPreview, value); }
    public string FileCount => (RecentScreenshots.Count + HeldFiles.Count).ToString();
    public bool AddingFiles { get => addingFiles; private set => Set(ref addingFiles, value); }
    public bool FileDragOver { get => fileDragOver; set => Set(ref fileDragOver, value); }
    public bool ToolsExpanded
    {
        get => preferences.ToolsExpanded;
        set
        {
            if (value == preferences.ToolsExpanded) return;
            preferences = preferences with { ToolsExpanded = value }; SavePreferences(); Changed();
        }
    }
    public string ScreenshotFolderLabel => preferences.ScreenshotFolder ?? "Windows Screenshots";
    public event Action? PreferencesChanged;
    public event Action? AppearanceChanged;
    public double PanelHeight
    {
        get => double.IsFinite(preferences.PanelHeight) ? preferences.PanelHeight : PanelLayout.PreferredHeight;
        set
        {
            value = Math.Round(Math.Clamp(double.IsFinite(value) ? value : PanelLayout.PreferredHeight, 180, 4000));
            if (value == preferences.PanelHeight) return;
            preferences = preferences with { PanelHeight = value };
            preferencesDirty = true;
            preferencesTimer.Stop(); preferencesTimer.Start();
            Changed();
        }
    }
    public double GlassAmount
    {
        get => preferences.GlassAmount;
        set
        {
            value = Math.Round(Math.Clamp(double.IsFinite(value) ? value : 55, 0, 100));
            if (value == preferences.GlassAmount) return;
            preferences = preferences with { GlassAmount = value };
            preferencesDirty = true;
            preferencesTimer.Stop(); preferencesTimer.Start();
            Changed(); Changed(nameof(GlassLabel));
            if (!appearanceTimer.IsEnabled) appearanceTimer.Start();
        }
    }
    public string GlassLabel => GlassAmount == 0 ? "Off" : $"{GlassAmount:0}%";
    public event Action? FocusCompleted;
    public string Network { get => network; private set => Set(ref network, value); }
    public string Bluetooth { get => bluetooth; private set => Set(ref bluetooth, value); }
    public string IntegrationStatus { get => integrationStatus; set => Set(ref integrationStatus, value); }
    public string Notice { get => notice; set { Set(ref notice, value); Changed(nameof(HasNotice)); } }
    public bool HasNotice => Notice.Length > 0;
    public string OutputName { get => outputName; private set => Set(ref outputName, value); }
    public bool AudioAvailable { get => audioAvailable; private set => Set(ref audioAvailable, value); }
    public bool Muted { get => muted; private set { Set(ref muted, value); Changed(nameof(VolumeGlyph)); } }
    public string VolumeGlyph => Muted ? "\uE74F" : "\uE767";
    public double Volume
    {
        get => volume;
        set
        {
            if (!Set(ref volume, value) || syncing) return;
            volumePending = true;
            if (!volumeTimer.IsEnabled) volumeTimer.Start();
        }
    }
    public bool BrightnessAvailable { get => brightnessAvailable; private set => Set(ref brightnessAvailable, value); }
    public string BrightnessDescription { get => brightnessDescription; private set => Set(ref brightnessDescription, value); }
    public double Brightness
    {
        get => brightness;
        set
        {
            if (!Set(ref brightness, value) || syncing) return;
            brightnessRevision++;
            brightnessPending = true;
            if (!brightnessWriting && !brightnessTimer.IsEnabled) brightnessTimer.Start();
        }
    }
    public string Note
    {
        get => note;
        set
        {
            if (!Set(ref note, value)) return;
            noteDirty = true;
            NoteStatus = "Saving…";
            Changed(nameof(NoteCount));
            Changed(nameof(CanAddNote));
            noteTimer.Stop();
            noteTimer.Start();
        }
    }
    public string NoteStatus { get => noteStatus; private set => Set(ref noteStatus, value); }
    public string NoteCount => $"{Note.Length:N0} characters";
    public bool AutoHeadphones
    {
        get => preferences.AutoHeadphones;
        set { preferences = preferences with { AutoHeadphones = value }; SavePreferences(); Changed(); }
    }
    public bool ReplaceQuickSettings
    {
        get => preferences.ReplaceQuickSettings;
        set { preferences = preferences with { ReplaceQuickSettings = value }; SavePreferences(); Changed(); }
    }
    public MediaState Media { get => mediaState; private set { if (Set(ref mediaState, value)) Changed(nameof(PlayGlyph)); } }
    public string PlayGlyph => Media.Playing ? "\uE769" : "\uE768";
    public string FocusLabel { get => focusLabel; private set => Set(ref focusLabel, value); }
    public bool FocusRunning => focusEnds.HasValue;

    public PanelViewModel(LocalStore store, Dispatcher dispatcher)
    {
        this.store = store;
        this.dispatcher = dispatcher;
        ShelfStore = new FileShelfStore(store);
        ClipboardStore = new ClipboardHistoryStore(store);
        noteTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(450), DispatcherPriority.Background, (_, _) => SaveNote(), dispatcher);
        volumeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(32), DispatcherPriority.Input,
            (_, _) => FlushVolume(), dispatcher);
        brightnessTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(75), DispatcherPriority.Background,
            async (_, _) => await FlushBrightnessAsync(), dispatcher);
        preferencesTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background,
            (_, _) => FlushPreferences(), dispatcher);
        appearanceTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render,
            (_, _) => { appearanceTimer!.Stop(); AppearanceChanged?.Invoke(); }, dispatcher);
        focusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => TickFocus(), dispatcher);
        copiedTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1400), DispatcherPriority.Background, (_, _) =>
        {
            copiedTimer!.Stop(); copiedClipboardId = null;
            foreach (var item in clipboardItems) item.Copied = false;
        }, dispatcher);
        noteTimer.Stop(); brightnessTimer.Stop(); focusTimer.Stop(); volumeTimer.Stop(); preferencesTimer.Stop(); appearanceTimer.Stop(); copiedTimer.Stop();
        try { preferences = store.LoadPreferences(); }
        catch (Exception ex) { Notice = "Could not read preferences: " + ex.Message; }
        try
        {
            notebook = store.LoadNotes();
            note = notebook.Draft;
            foreach (var item in notebook.Entries.OrderByDescending(item => item.UpdatedAt)) Notes.Add(item);
            notesReady = true;
        }
        catch (Exception ex) { Notice = "Could not read notes: " + ex.Message; }
    }

    private void FlushVolume()
    {
        volumeTimer.Stop();
        if (!volumePending) return;
        volumePending = false;
        Try(() => audio?.SetVolume(Volume));
    }

    public void BeginVolumeFeedback()
    {
        if (VolumeFeedbackEnabled && audio?.DefaultOutputId is { } id) volumeFeedback.Begin(id);
    }

    public void CommitVolume()
    {
        FlushVolume();
        if (VolumeFeedbackEnabled && Volume > 0 && !Muted && audio?.DefaultOutputId is { } id) volumeFeedback.Commit(id);
        else volumeFeedback.Cancel();
    }

    public void CancelVolumeFeedback() => volumeFeedback.Cancel();
    internal int VolumeFeedbackCount => volumeFeedback.TriggeredCount;
    internal bool VolumeFeedbackReady => volumeFeedback.OutputReady;
    internal uint ClipboardCopySequence => clipboard?.CopiedSequence ?? 0;

    private async Task FlushBrightnessAsync()
    {
        brightnessTimer.Stop();
        if (!brightnessPending || brightnessWriting) return;
        brightnessPending = false;
        brightnessWriting = true;
        try { await TryAsync(() => brightnessService.SetAsync((int)Brightness)); }
        finally
        {
            brightnessWriting = false;
            if (!disposed && brightnessPending) brightnessTimer.Start();
        }
    }

    public async Task InitializeShelfAsync(IntPtr window, bool captureMode)
    {
        await TryAsync(async () =>
        {
            await RefreshShelfAsync();
            if (captureMode || disposed) return;
            screenshots = new ScreenshotService(ShelfStore, dispatcher, window);
            screenshots.Changed += async () => await TryAsync(RefreshShelfAsync);
            screenshots.Failed += message => Notice = message;
            screenshots.Watch(preferences.ScreenshotFolder);
        });
    }

    public void ArmScreenshot() => screenshots?.ArmCapture();
    public void ClipboardChanged() => screenshots?.ClipboardChanged();

    public async Task InitializeClipboardAsync(bool captureMode)
    {
        await TryAsync(async () =>
        {
            await RefreshClipboardAsync();
            if (captureMode || disposed) return;
            clipboard = new ClipboardHistoryService(ClipboardStore, dispatcher, ClipboardHistoryEnabled);
            clipboard.Changed += async () => await TryAsync(RefreshClipboardAsync);
            clipboard.Failed += message => Notice = message;
            await clipboard.Ready;
        });
    }

    private async Task RefreshClipboardAsync()
    {
        var revision = ++clipboardRefreshRevision;
        var records = await ClipboardStore.LoadAsync();
        var old = clipboardItems.ToDictionary(item => item.Entry.Id);
        var next = await Task.Run(() => records.Select(record =>
        {
            if (old.TryGetValue(record.Id, out var cached) && cached.Entry == record) return cached;
            var thumbnail = cached?.Thumbnail;
            if (record.Kind == ClipboardItemKind.Image && thumbnail is null)
            {
                try { thumbnail = ScreenshotService.LoadImage(ClipboardStore.ImagePath(record.Id), thumbnail: true); }
                catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
            }
            return new ClipboardItemViewModel(record) { Thumbnail = thumbnail };
        }).ToArray());
        if (disposed || revision != clipboardRefreshRevision) return;
        clipboardItems = next;
        foreach (var item in clipboardItems) item.Copied = item.Entry.Id == copiedClipboardId;
        FilterClipboard();
        Changed(nameof(HasClipboardItems)); Changed(nameof(ClipboardSummary));
    }

    private void FilterClipboard()
    {
        var query = ClipboardSearch.Trim();
        ReconcileItems(ClipboardItems, clipboardItems.Where(item => query.Length == 0 ||
            (item.Entry.Text ?? item.Details).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray());
        Changed(nameof(HasClipboardResults)); Changed(nameof(ClipboardEmptyMessage));
    }

    public async Task CopyClipboardItemAsync(ClipboardItemViewModel item) => await TryAsync(async () =>
    {
        if (clipboard is null) throw new InvalidOperationException("Clipboard history is still starting.");
        await clipboard.CopyAsync(item.Entry.Id);
        copiedClipboardId = item.Entry.Id;
        await RefreshClipboardAsync();
        copiedTimer.Stop(); copiedTimer.Start();
    });

    public async Task RemoveClipboardItemAsync(ClipboardItemViewModel item) => await TryAsync(async () =>
    {
        await ClipboardStore.RemoveAsync(item.Entry.Id);
        await RefreshClipboardAsync();
    });

    public async Task ClearClipboardHistoryAsync() => await TryAsync(async () =>
    {
        if (clipboard is not null) await clipboard.ClearAsync(); else await ClipboardStore.ClearAsync();
        await RefreshClipboardAsync();
    });
    public void SetScreenshotFolder(string folder)
    {
        preferences = preferences with { ScreenshotFolder = folder };
        SavePreferences(); Changed(nameof(ScreenshotFolderLabel));
        screenshots?.Watch(folder);
    }

    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        pendingFileBatches++;
        AddingFiles = true;
        await fileImports.WaitAsync();
        var failed = new List<string>();
        try
        {
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try { await ShelfStore.AddPathAsync(path); }
                catch (Exception ex) { failed.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
            }
            await RefreshShelfAsync();
            if (failed.Count > 0) Notice = "Could not add " + string.Join("; ", failed);
        }
        catch (Exception ex) { Notice = "Could not update the shelf: " + ex.Message; }
        finally
        {
            fileImports.Release();
            if (--pendingFileBatches == 0) AddingFiles = false;
        }
    }

    public async Task RemoveFileAsync(ShelfItemViewModel item) => await TryAsync(async () =>
    {
        await ShelfStore.RemoveAsync(item.Entry.Id);
        await RefreshShelfAsync();
    });

    public async Task KeepScreenshotAsync(ShelfItemViewModel item) => await TryAsync(async () =>
    {
        await ShelfStore.KeepAsync(item.Entry.Id);
        await RefreshShelfAsync();
    });

    private async Task RefreshShelfAsync()
    {
        var revision = ++shelfRefreshRevision;
        var records = await ShelfStore.LoadAsync();
        var old = RecentScreenshots.Concat(HeldFiles).ToDictionary(item => item.Entry.Id);
        var items = await Task.Run(() => records.Select(record =>
        {
            if (old.TryGetValue(record.Id, out var cached) && cached.Entry == record) return cached;
            var item = new ShelfItemViewModel(record, ShelfStore.GetPath(record));
            if (cached?.Thumbnail is not null) item.Thumbnail = cached.Thumbnail;
            else if (ScreenshotService.IsImage(item.FullPath))
            {
                try { item.Thumbnail = ScreenshotService.LoadImage(item.FullPath, thumbnail: true); }
                catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
            }
            return item;
        }).ToArray());
        if (disposed || revision != shelfRefreshRevision) return;
        ReconcileItems(RecentScreenshots, items.Where(item => item.IsScreenshot).ToArray());
        ReconcileItems(HeldFiles, items.Where(item => !item.IsScreenshot).ToArray());
        Changed(nameof(HasScreenshots)); Changed(nameof(HasHeldFiles));
        Changed(nameof(LatestScreenshot)); Changed(nameof(FileCount));
        if (LatestScreenshot?.Entry.Id != latestPreviewId) _ = LoadLatestPreviewAsync(LatestScreenshot);
    }

    private async Task LoadLatestPreviewAsync(ShelfItemViewModel? item)
    {
        latestPreviewId = item?.Entry.Id;
        LatestScreenshotPreview = item?.Thumbnail;
        if (item is null) return;
        try
        {
            var image = await Task.Run(() => ScreenshotService.LoadImage(item.FullPath, thumbnail: true, decodeWidth: 960));
            if (!disposed && latestPreviewId == item.Entry.Id) LatestScreenshotPreview = image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
    }

    private static void ReconcileItems<T>(ObservableCollection<T> target, IReadOnlyList<T> next) where T : class
    {
        for (var i = 0; i < next.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], next[i])) continue;
            var existing = target.IndexOf(next[i]);
            if (existing >= 0) target.Move(existing, i);
            else target.Insert(i, next[i]);
        }
        while (target.Count > next.Count) target.RemoveAt(target.Count - 1);
    }

    public async Task InitializeAsync(IntPtr monitor)
    {
        Try(() =>
        {
            audio = new AudioService(dispatcher);
            audio.Changed += SyncAudio;
            audio.DevicesChanged += (before, after) =>
            {
                var candidate = HeadphoneRouting.Choose(before, after, AutoHeadphones, preferences.PreferredHeadphonesId);
                if (candidate is not null) SelectOutput(candidate);
            };
            SyncAudio();
        });
        media = new MediaService(dispatcher);
        media.Changed += () => Media = media.State;
        await Task.WhenAll(TryAsync(() => media.InitializeAsync()), RefreshEnvironmentAsync(monitor));
    }

    public async Task RefreshEnvironmentAsync(IntPtr monitor)
    {
        var revision = brightnessRevision;
        Network = await Task.Run(SystemActions.NetworkLabel);
        Bluetooth = await SystemActions.BluetoothLabelAsync();
        await TryAsync(async () =>
        {
            var state = await brightnessService.ReadAsync(monitor);
            syncing = true;
            BrightnessAvailable = state.Available;
            BrightnessDescription = state.Description;
            if (revision == brightnessRevision) Brightness = state.Value;
            syncing = false;
        });
    }

    private void SyncAudio()
    {
        if (audio is null) return;
        Try(() =>
        {
            syncing = true;
            AudioAvailable = audio.Available;
            if (!volumePending) Volume = Math.Round(audio.Volume * 100);
            Muted = audio.Muted;
            if (!ReferenceEquals(displayedOutputs, audio.Outputs))
            {
                displayedOutputs = audio.Outputs;
                OutputName = audio.Outputs.FirstOrDefault(d => d.IsDefault)?.Name ?? "No audio output";
                Outputs.Clear();
                foreach (var output in audio.Outputs) Outputs.Add(output);
            }
            syncing = false;
        });
        syncing = false;
    }

    public void SelectOutput(AudioOutput output) => Try(() => audio?.SelectOutput(output.Id));
    public void PreferHeadphones(AudioOutput output)
    {
        preferences = preferences with { PreferredHeadphonesId = output.Id, AutoHeadphones = true };
        SavePreferences();
        Changed(nameof(AutoHeadphones));
        Notice = "Preferred headphones: " + output.Name;
    }
    public void ToggleMute() => Try(() => audio?.ToggleMute());
    public Task PlayPauseAsync() => TransportAsync(() => media?.PlayPauseAsync() ?? Task.FromResult(false));
    public Task PreviousAsync() => TransportAsync(() => media?.PreviousAsync() ?? Task.FromResult(false));
    public Task NextAsync() => TransportAsync(() => media?.NextAsync() ?? Task.FromResult(false));
    private async Task TransportAsync(Func<Task<bool>> action)
    {
        await TryAsync(async () => { if (!await action()) Notice = "This player does not support that command."; });
    }

    private void SavePreferences()
    {
        preferencesDirty = true;
        FlushPreferences();
        PreferencesChanged?.Invoke();
    }

    private void FlushPreferences()
    {
        preferencesTimer.Stop();
        if (!preferencesDirty) return;
        try { store.SavePreferences(preferences); preferencesDirty = false; }
        catch (Exception ex) { Notice = "Could not save preferences: " + ex.Message; }
    }

    public bool SaveNote()
    {
        noteTimer.Stop();
        if (!noteDirty) return true;
        if (!notesReady) return false;
        var next = editingNote is null
            ? notebook with { Draft = Note }
            : notebook with { Entries = notebook.Entries.Select(item => item.Id == editingNote.Id
                ? item with { Content = Note, UpdatedAt = DateTimeOffset.UtcNow } : item).ToList() };
        if (!CommitNotes(next)) return false;
        if (editingNote is not null) editingNote = notebook.Entries.First(item => item.Id == editingNote.Id);
        noteDirty = false;
        NoteStatus = IsEditingNote ? "Saved" : "Draft saved";
        Changed(nameof(EditingNoteLabel));
        return true;
    }

    public bool AddNote()
    {
        if (!CanAddNote) return false;
        var now = DateTimeOffset.UtcNow;
        var entry = new QuickNote(Guid.NewGuid(), Note, now, now);
        if (!CommitNotes(notebook with { Draft = "", Entries = notebook.Entries.Prepend(entry).ToList() })) return false;
        SetNoteBuffer("");
        return true;
    }

    public bool EditNote(QuickNote entry)
    {
        if (!SaveNote()) return false;
        var current = notebook.Entries.FirstOrDefault(item => item.Id == entry.Id);
        if (current is null) return false;
        editingNote = current;
        SetNoteBuffer(current.Content);
        return true;
    }

    public bool ShowNotesList()
    {
        if (!SaveNote()) return false;
        editingNote = null;
        SetNoteBuffer(notebook.Draft);
        return true;
    }

    public bool DeleteNote(Guid id)
    {
        if (!notesReady) return false;
        if (editingNote?.Id != id && !SaveNote()) return false;
        if (!CommitNotes(notebook with { Entries = notebook.Entries.Where(item => item.Id != id).ToList() })) return false;
        if (editingNote?.Id == id) { editingNote = null; SetNoteBuffer(notebook.Draft); }
        return true;
    }

    public bool DeleteEditingNote() => editingNote is not null && DeleteNote(editingNote.Id);

    private bool CommitNotes(NotesDocument next)
    {
        try
        {
            store.SaveNotes(next);
            notebook = next;
            ReconcileItems(Notes, next.Entries.OrderByDescending(item => item.UpdatedAt).ToArray());
            Changed(nameof(HasNotes));
            return true;
        }
        catch (Exception ex) { NoteStatus = "Not saved"; Notice = "Could not save notes: " + ex.Message; return false; }
    }

    private void SetNoteBuffer(string content)
    {
        noteTimer.Stop();
        note = content;
        noteDirty = false;
        NoteStatus = "";
        Changed(nameof(Note)); Changed(nameof(NoteCount)); Changed(nameof(CanAddNote));
        Changed(nameof(IsEditingNote)); Changed(nameof(IsComposingNote)); Changed(nameof(EditingNoteLabel));
    }

    public void ToggleFocus()
    {
        if (focusEnds.HasValue) { focusEnds = null; focusTimer.Stop(); FocusLabel = "25 min"; }
        else { focusEnds = DateTimeOffset.Now.AddMinutes(25); focusTimer.Start(); TickFocus(); }
        Changed(nameof(FocusRunning));
    }

    private void TickFocus()
    {
        if (focusEnds is null) return;
        var left = focusEnds.Value - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero)
        {
            focusEnds = null; focusTimer.Stop(); FocusLabel = "Complete"; Changed(nameof(FocusRunning)); FocusCompleted?.Invoke();
        }
        else FocusLabel = $"{left.Minutes:00}:{left.Seconds:00}";
    }

    public void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) { Notice = ex.Message; }
    }
    public async Task TryAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Notice = ex.Message; }
    }

    public void Dispose()
    {
        SaveNote(); FlushPreferences(); FlushVolume();
        noteTimer.Stop(); brightnessTimer.Stop(); focusTimer.Stop(); volumeTimer.Stop(); preferencesTimer.Stop(); appearanceTimer.Stop(); copiedTimer.Stop();
        disposed = true;
        screenshots?.Dispose();
        clipboard?.Dispose(); volumeFeedback.Dispose();
        audio?.Dispose(); media?.Dispose();
    }
}
