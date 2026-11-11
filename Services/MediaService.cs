using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace BetterQuickSettings.Services;

public sealed record MediaState(string Title, string Artist, string Source, bool Available,
    bool Playing, bool CanPlay, bool CanPrevious, bool CanNext, BitmapSource? Artwork)
{
    public static MediaState Empty { get; } = new("", "", "", false, false, false, false, false, null);
}

public sealed class MediaService : IDisposable
{
    [Flags] private enum Refresh { None = 0, Session = 1, Metadata = 2, Playback = 4 }
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer debounce;
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    private GlobalSystemMediaTransportControlsSession? session;
    private bool disposed, refreshing;
    private Refresh pending;
    private string? artworkKey;
    public MediaState State { get; private set; } = MediaState.Empty;
    public event Action? Changed;

    public MediaService(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(70), DispatcherPriority.Background,
            async (_, _) => await DrainAsync(), dispatcher);
        debounce.Stop();
    }

    public async Task InitializeAsync()
    {
        manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        if (disposed) return;
        manager.CurrentSessionChanged += SessionChanged;
        manager.SessionsChanged += SessionsChanged;
        pending = Refresh.Session;
        await DrainAsync();
    }

    private void SessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => Queue(Refresh.Session);
    private void SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => Queue(Refresh.Session);
    private void PropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => Queue(Refresh.Metadata);
    private void PlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => Queue(Refresh.Playback);

    private void Queue(Refresh kind)
    {
        if (disposed || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(new Action(() =>
        {
            if (disposed) return;
            pending |= kind;
            if (!refreshing && !debounce.IsEnabled) debounce.Start();
        }));
    }

    private async Task DrainAsync()
    {
        debounce.Stop();
        if (disposed || refreshing) return;
        refreshing = true;
        var work = pending;
        pending = Refresh.None;
        try
        {
            if ((work & Refresh.Session) != 0)
            {
                var next = manager?.GetCurrentSession();
                if (!Equals(next, session))
                {
                    Detach();
                    session = next;
                    artworkKey = null;
                    if (session is not null)
                    {
                        session.MediaPropertiesChanged += PropertiesChanged;
                        session.PlaybackInfoChanged += PlaybackChanged;
                    }
                    work |= Refresh.Metadata | Refresh.Playback;
                }
            }
            var current = session;
            if (current is null) { Publish(MediaState.Empty); return; }
            if (!State.Available) work |= Refresh.Metadata | Refresh.Playback;

            var state = State;
            if ((work & Refresh.Metadata) != 0)
            {
                var properties = await current.TryGetMediaPropertiesAsync();
                var key = $"{current.SourceAppUserModelId}\n{properties.Title}\n{properties.Artist}\n{properties.AlbumTitle}";
                var art = state.Artwork;
                if (key != artworkKey)
                {
                    art = null;
                    if (properties.Thumbnail is not null)
                    {
                        // Album art is decoded only for a new track, on a worker, at its display size.
                        var thumbnail = properties.Thumbnail;
                        try
                        {
                            art = await Task.Run(async () =>
                            {
                                using var random = await thumbnail.OpenReadAsync();
                                using var stream = random.AsStreamForRead();
                                var image = new BitmapImage();
                                image.BeginInit();
                                image.CacheOption = BitmapCacheOption.OnLoad;
                                image.DecodePixelWidth = 80;
                                image.StreamSource = stream;
                                image.EndInit();
                                image.Freeze();
                                return image;
                            });
                        }
                        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or ArgumentException or NotSupportedException)
                        { art = null; }
                    }
                    artworkKey = key;
                }
                var source = current.SourceAppUserModelId;
                source = source.Contains("Spotify", StringComparison.OrdinalIgnoreCase) ? "SPOTIFY" :
                    source.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ? "CHROME" :
                    source.Contains("Edge", StringComparison.OrdinalIgnoreCase) ? "EDGE" : "MEDIA";
                state = state with { Title = properties.Title, Artist = properties.Artist, Source = source, Available = true, Artwork = art };
            }
            if ((work & (Refresh.Metadata | Refresh.Playback)) != 0)
            {
                var playback = current.GetPlaybackInfo();
                state = state with
                {
                    Playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    CanPlay = playback.Controls.IsPlayPauseToggleEnabled,
                    CanPrevious = playback.Controls.IsPreviousEnabled,
                    CanNext = playback.Controls.IsNextEnabled
                };
            }
            // This UI has no progress bar: timeline ticks need no subscription or background work.
            if (!disposed) Publish(state);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or ArgumentException or NotSupportedException)
        {
            artworkKey = null;
            if (!disposed) Publish(MediaState.Empty);
        }
        finally
        {
            refreshing = false;
            if (!disposed && pending != Refresh.None) debounce.Start();
        }
    }

    private void Publish(MediaState next)
    {
        if (State == next) return;
        State = next;
        Changed?.Invoke();
    }

    public async Task<bool> PlayPauseAsync() => session is not null && await session.TryTogglePlayPauseAsync();
    public async Task<bool> NextAsync() => session is not null && await session.TrySkipNextAsync();
    public async Task<bool> PreviousAsync() => session is not null && await session.TrySkipPreviousAsync();

    private void Detach()
    {
        if (session is null) return;
        session.MediaPropertiesChanged -= PropertiesChanged;
        session.PlaybackInfoChanged -= PlaybackChanged;
        session = null;
    }

    public void Dispose()
    {
        disposed = true;
        debounce.Stop();
        Detach();
        if (manager is not null)
        {
            manager.CurrentSessionChanged -= SessionChanged;
            manager.SessionsChanged -= SessionsChanged;
        }
    }
}
