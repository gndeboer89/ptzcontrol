using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using PtzJoystickControl.Core.Video;
using ReactiveUI;

namespace PtzJoystickControl.Gui.ViewModels;

/// <summary>Plays one camera's video stream with LibVLC.</summary>
public class VideoViewModel : ViewModelBase, IDisposable
{
    private readonly string _url;
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private string _status = "Starting...";
    private bool _isPlaying;
    private bool _disposed;

    public VideoViewModel(string cameraName, string? url)
    {
        CameraName = cameraName;
        _url = url?.Trim() ?? "";
        UrlForDisplay = VideoSourceUrl.RedactCredentials(_url);
        ReconnectCommand = ReactiveCommand.Create(Start);
        Start();
    }

    public string CameraName { get; }
    public string UrlForDisplay { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ReconnectCommand { get; }

    public MediaPlayer? MediaPlayer
    {
        get => _mediaPlayer;
        private set => this.RaiseAndSetIfChanged(ref _mediaPlayer, value);
    }

    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => this.RaiseAndSetIfChanged(ref _isPlaying, value);
    }

    private void Start()
    {
        if (_disposed)
            return;

        switch (VideoSourceUrl.Classify(_url))
        {
            case VideoSourceKind.None:
                Status = "No video URL set for this camera. Enter an rtsp:// address in the camera settings.";
                return;
            case VideoSourceKind.Invalid:
                Status = "This is not a valid video URL. Example: rtsp://192.168.1.10:554/stream1";
                return;
            case VideoSourceKind.Ndi:
                Status = "NDI sources are not supported yet.";
                return;
        }

        try
        {
            // Throws when the native VLC libraries cannot be found.
            LibVLCSharp.Shared.Core.Initialize();
            _libVlc ??= new LibVLC("--no-osd", "--no-video-title-show");

            if (_mediaPlayer == null)
            {
                var player = new MediaPlayer(_libVlc);
                player.Playing += (_, _) => OnUi(() => { IsPlaying = true; Status = "Playing"; });
                player.Buffering += (_, e) => OnUi(() =>
                {
                    if (e.Cache < 100) Status = $"Buffering {e.Cache:0}%";
                    else Status = "Playing";
                });
                player.EncounteredError += (_, _) => OnUi(() => { IsPlaying = false; Status = "Could not play the stream. Check the URL, username/password and that the camera is reachable."; });
                player.EndReached += (_, _) => OnUi(() => { IsPlaying = false; Status = "Stream ended."; });
                MediaPlayer = player;
            }

            // Prefer TCP-interleaved RTSP (survives Wi-Fi/firewalls) and a small buffer for low latency.
            using var media = new Media(_libVlc, new Uri(_url));
            media.AddOption(":rtsp-tcp");
            media.AddOption(":network-caching=150");
            media.AddOption(":live-caching=150");

            Status = "Connecting...";
            // Play can block on network I/O; keep the UI responsive.
            var player2 = _mediaPlayer!;
            Task.Run(() => player2.Play(media));
        }
        catch (Exception e) when (e is VLCException or DllNotFoundException or BadImageFormatException)
        {
            Status = "The video component (VLC) could not be loaded: " + e.Message;
        }
    }

    private static void OnUi(Action action) => Dispatcher.UIThread.Post(action);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        var player = _mediaPlayer;
        var libVlc = _libVlc;
        _mediaPlayer = null;
        _libVlc = null;

        // Stopping can block and must not run on the UI thread or inside a VLC event callback.
        Task.Run(() =>
        {
            try
            {
                player?.Stop();
                player?.Dispose();
                libVlc?.Dispose();
            }
            catch (Exception e)
            {
                System.Diagnostics.Debug.WriteLine(e);
            }
        });
    }
}
