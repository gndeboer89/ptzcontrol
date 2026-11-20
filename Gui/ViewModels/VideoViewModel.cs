using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using PtzJoystickControl.Core.Video;
using PtzJoystickControl.Gui.Video;
using ReactiveUI;

namespace PtzJoystickControl.Gui.ViewModels;

/// <summary>Plays one camera's video: RTSP (and similar) through LibVLC, or NDI through the NDI runtime.</summary>
public class VideoViewModel : ViewModelBase, IDisposable
{
    private readonly string _url;
    private MediaPlayer? _mediaPlayer;
    private NdiReceiver? _ndi;
    private WriteableBitmap? _frame;
    private byte[] _frameBuffer = Array.Empty<byte>();
    private int _ndiUiPending;
    private string _status = "Starting...";
    private bool _isPlaying;
    private bool _disposed;

    public VideoViewModel(string cameraName, string? url, object? camera = null)
    {
        CameraName = cameraName;
        Camera = camera;
        _url = url?.Trim() ?? "";
        UrlForDisplay = VideoSourceUrl.RedactCredentials(_url);
        Kind = VideoSourceUrl.Classify(_url);
        ReconnectCommand = ReactiveCommand.Create(Start);
        Start();
    }

    public string CameraName { get; }

    /// <summary>Raised on the UI thread after new NDI pixels were written into <see cref="Frame"/>.</summary>
    public event Action? FrameUpdated;

    /// <summary>The camera this video belongs to (lets a multiview tile select it).</summary>
    public object? Camera { get; }

    public string UrlForDisplay { get; }
    public VideoSourceKind Kind { get; }
    public bool UsesVlc => Kind is VideoSourceKind.Rtsp or VideoSourceKind.Other;
    public bool UsesNdi => Kind == VideoSourceKind.Ndi;
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ReconnectCommand { get; }

    public MediaPlayer? MediaPlayer
    {
        get => _mediaPlayer;
        private set => this.RaiseAndSetIfChanged(ref _mediaPlayer, value);
    }

    /// <summary>Latest NDI frame (NDI sources only).</summary>
    public WriteableBitmap? Frame
    {
        get => _frame;
        private set => this.RaiseAndSetIfChanged(ref _frame, value);
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

        switch (Kind)
        {
            case VideoSourceKind.None:
                Status = "No video URL set. Enter an rtsp:// address or pick an NDI source.";
                return;
            case VideoSourceKind.Invalid:
                Status = "This is not a valid video URL. Example: rtsp://192.168.1.10:554/stream1";
                return;
            case VideoSourceKind.Ndi:
                StartNdi();
                return;
            default:
                StartVlc();
                return;
        }
    }

    // ---- RTSP / other via LibVLC -------------------------------------------------------------

    private void StartVlc()
    {
        try
        {
            var libVlc = VlcHost.Instance;

            if (_mediaPlayer == null)
            {
                var player = new MediaPlayer(libVlc);
                player.Playing += (_, _) => OnUi(() => { IsPlaying = true; Status = "Playing"; });
                player.Buffering += (_, e) => OnUi(() => Status = e.Cache < 100 ? $"Buffering {e.Cache:0}%" : "Playing");
                player.EncounteredError += (_, _) => OnUi(() => { IsPlaying = false; Status = "Could not play the stream. Check the URL, username/password and that the camera is reachable."; });
                player.EndReached += (_, _) => OnUi(() => { IsPlaying = false; Status = "Stream ended."; });
                MediaPlayer = player;
            }

            // Prefer TCP-interleaved RTSP (survives Wi-Fi/firewalls) and a small buffer for low latency.
            // The media must stay alive until Play has run, so it is released inside the task below, not here.
            var media = new Media(libVlc, new Uri(_url));
            media.AddOption(":rtsp-tcp");
            media.AddOption(":network-caching=150");
            media.AddOption(":live-caching=150");

            Status = "Connecting...";
            var player2 = _mediaPlayer!;
            // Play can block on network I/O; keep the UI responsive.
            Task.Run(() =>
            {
                try
                {
                    if (!_disposed)
                        player2.Play(media); // the player keeps its own reference to the media
                }
                catch (Exception e) when (e is VLCException or ObjectDisposedException or InvalidOperationException)
                {
                    OnUi(() => Status = "Could not start the stream: " + e.Message);
                }
                finally
                {
                    media.Dispose();
                }
            });
        }
        catch (Exception e)
        {
            // One failing tile must never take the whole program (or the multiview) down with it.
            CrashLog.Write("Starting video failed for " + CameraName, e);
            Status = "The video could not be started: " + e.Message;
        }
    }

    // ---- NDI ---------------------------------------------------------------------------------

    private void StartNdi()
    {
        var name = VideoSourceUrl.NdiSourceName(_url);
        if (name == null)
        {
            Status = "Invalid NDI source.";
            return;
        }

        StopNdi();
        Status = "Connecting...";
        var receiver = new NdiReceiver(name);
        receiver.StatusChanged += text => OnUi(() =>
        {
            Status = text;
            IsPlaying = text.StartsWith("Playing", StringComparison.Ordinal);
        });
        receiver.FrameAvailable += () =>
        {
            // At most one UI update in flight; newer frames simply replace older ones.
            if (System.Threading.Interlocked.Exchange(ref _ndiUiPending, 1) == 0)
                Dispatcher.UIThread.Post(() => ShowNdiFrame(receiver), DispatcherPriority.Render);
        };
        _ndi = receiver;
        receiver.Start();
    }

    private void ShowNdiFrame(NdiReceiver receiver)
    {
        System.Threading.Interlocked.Exchange(ref _ndiUiPending, 0);
        if (_disposed || receiver != _ndi)
            return;
        if (!receiver.TryTakeFrame(ref _frameBuffer, out var width, out var height))
            return;

        if (_frame == null || _frame.PixelSize.Width != width || _frame.PixelSize.Height != height)
            Frame = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

        using var locked = _frame!.Lock();
        FrameCopy.ToNative(_frameBuffer, height, width * 4, locked.Address, locked.RowBytes);
        FrameUpdated?.Invoke();
    }

    private void StopNdi()
    {
        var receiver = _ndi;
        _ndi = null;
        if (receiver != null)
            Task.Run(receiver.Dispose); // joins the receiver thread; keep that off the UI thread
    }

    // ---- shared ------------------------------------------------------------------------------

    private static void OnUi(Action action) => Dispatcher.UIThread.Post(action);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        StopNdi();

        var player = _mediaPlayer;
        _mediaPlayer = null;
        if (player == null)
            return;

        // Stopping can block and must not run on the UI thread or inside a VLC event callback.
        Task.Run(() =>
        {
            try
            {
                player.Stop();
                player.Dispose();
            }
            catch (Exception e)
            {
                System.Diagnostics.Debug.WriteLine(e);
            }
        });
    }
}
