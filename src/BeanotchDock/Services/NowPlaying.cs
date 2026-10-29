using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Control;

namespace BeanotchDock.Services;

// What's playing, from Windows' own media controls (Spotify, a browser, Media Player…): the song, its cover and whether
// it's playing, for the dock's Now Playing tile. Changes arrive on Windows' threads and are passed to the UI's.
public sealed class NowPlaying(DispatcherQueue ui)
{
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    private GlobalSystemMediaTransportControlsSession? session;

    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public bool Playing { get; private set; }
    public BitmapImage? Cover { get; private set; }
    public bool HasTrack => Title.Length > 0;
    public event Action? Changed;

    public async Task StartAsync()
    {
        try
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            manager.CurrentSessionChanged += (_, _) => Watch();
            Watch();
        }
        catch (Exception e) { App.Log(e); }
    }

    private void Watch()
    {
        if (session is not null)
        {
            session.MediaPropertiesChanged -= OnProperties;
            session.PlaybackInfoChanged -= OnPlayback;
        }
        session = manager?.GetCurrentSession();
        if (session is not null)
        {
            session.MediaPropertiesChanged += OnProperties;
            session.PlaybackInfoChanged += OnPlayback;
        }
        _ = ReadAsync();
    }

    private void OnProperties(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ = ReadAsync();
    private void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => _ = ReadAsync();

    private async Task ReadAsync()
    {
        var current = session;
        string title = "", artist = "";
        var playing = false;
        Windows.Storage.Streams.IRandomAccessStreamReference? thumbnail = null;
        try
        {
            if (current is not null)
            {
                var props = await current.TryGetMediaPropertiesAsync();
                title = props?.Title ?? "";
                artist = props?.Artist ?? "";
                thumbnail = props?.Thumbnail;
                playing = current.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
        }
        catch (Exception) { }
        var song = title != Title || artist != Artist;
        Windows.Storage.Streams.IRandomAccessStreamWithContentType? stream = null;
        if (song && thumbnail is not null)
            try { stream = await thumbnail.OpenReadAsync(); } catch (Exception) { stream = null; }
        ui.TryEnqueue(async () =>
        {
            (Title, Artist, Playing) = (title, artist, playing);
            if (song)
            {
                Cover = null;
                if (stream is not null)
                    try
                    {
                        var image = new BitmapImage();
                        await image.SetSourceAsync(stream);
                        Cover = image;
                    }
                    catch (Exception) { }
            }
            Changed?.Invoke();
        });
    }

    public void TogglePlay() => _ = session?.TryTogglePlayPauseAsync();
    public void Next() => _ = session?.TrySkipNextAsync();
    public void Previous() => _ = session?.TrySkipPreviousAsync();
}
