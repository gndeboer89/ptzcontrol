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
    private VlcFrameSink? _vlcSink;
    private NdiReceiver? _ndi;
    private WriteableBitmap? _frame;
    private byte[] _frameBuffer = Array.Empty<byte>();
    private int _uiPending;
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

    /// <summary>The latest picture, for RTSP/other streams (LibVLC) and NDI alike.</summary>
    public WriteableBitmap? Frame
    {
        get => _frame;
        private set => this.RaiseAndSetIfChanged(ref _frame, value);
    }

    /// <summary>
    /// Size the picture should be shown at. LibVLC may hand over frames a few lines taller than the real picture
    /// (for example 386 lines for a 360-line video); showing them at the source's own size keeps the proportions right.
    /// </summary>
    public double DisplayWidth
    {
        get => _displayWidth;
        private set => this.RaiseAndSetIfChanged(ref _displayWidth, value);
    }

    public double DisplayHeight
    {
        get => _displayHeight;
        private set => this.RaiseAndSetIfChanged(ref _displayHeight, value);
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
                // Frames come to us as pixels (no native child window), and cameras are watched, not listened to.
                var sink = new VlcFrameSink();
                sink.FrameAvailable += () => QueueFrameUpdate(() => sink.TryTakeFrame(ref _frameBuffer, out _frameWidth, out _frameHeight));
                sink.Attach(player);
                _vlcSink = sink;
                player.Playing += (_, _) => OnUi(() => { IsPlaying = true; Status = "Playing"; });
                player.Buffering += (_, e) => OnUi(() => Status = e.Cache < 100 ? $"Buffering {e.Cache:0}%" : "Playing");
                player.EncounteredError += (_, _) => OnUi(() => { IsPlaying = false; Status = "Could not play the stream. Check the URL, username/password and that the camera is reachable."; });
                player.EndReached += (_, _) => OnUi(() => { IsPlaying = false; Status = "Stream ended."; });
                _mediaPlayer = player;
            }

            // Prefer TCP-interleaved RTSP (survives Wi-Fi/firewalls) and a small buffer for low latency.
            // The media must stay alive until Play has run, so it is released inside the task below, not here.
            var media = new Media(libVlc, new Uri(_url));
            media.AddOption(":no-audio");
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
        receiver.FrameAvailable += () => QueueFrameUpdate(() => receiver == _ndi && receiver.TryTakeFrame(ref _frameBuffer, out _frameWidth, out _frameHeight));
        _ndi = receiver;
        receiver.Start();
    }

    // ---- showing frames (both sources) -------------------------------------------------------

    private int _frameWidth, _frameHeight;
    private double _displayWidth = 16, _displayHeight = 9;

    /// <summary>Called on a background thread when a new frame exists; shows it on the UI thread.</summary>
    private void QueueFrameUpdate(Func<bool> takeFrame)
    {
        // At most one UI update in flight; newer frames simply replace older ones.
        if (System.Threading.Interlocked.Exchange(ref _uiPending, 1) == 0)
            Dispatcher.UIThread.Post(() => ShowFrame(takeFrame), DispatcherPriority.Render);
    }

    private void ShowFrame(Func<bool> takeFrame)
    {
        System.Threading.Interlocked.Exchange(ref _uiPending, 0);
        if (_disposed || !takeFrame())
            return;

        int width = _frameWidth, height = _frameHeight;
        if (width <= 0 || height <= 0)
            return;

        if (_frame == null || _frame.PixelSize.Width != width || _frame.PixelSize.Height != height)
            Frame = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

        UpdateDisplaySize(width, height);

        using var locked = _frame!.Lock();
        FrameCopy.ToNative(_frameBuffer, height, width * 4, locked.Address, locked.RowBytes);
        FrameUpdated?.Invoke();
    }

    private void UpdateDisplaySize(int frameWidth, int frameHeight)
    {
        double w = frameWidth, h = frameHeight;

        // For LibVLC streams ask for the real picture size; it is only known once the video output exists.
        var player = _mediaPlayer;
        if (player != null)
        {
            uint sourceWidth = 0, sourceHeight = 0;
            try
            {
                if (player.Size(0, ref sourceWidth, ref sourceHeight) && sourceWidth > 0 && sourceHeight > 0)
                {
                    w = sourceWidth;
                    h = sourceHeight;
                }
            }
            catch (Exception e) when (e is VLCException or ObjectDisposedException)
            {
                // Keep the frame size.
            }
        }

        if (Math.Abs(w - _displayWidth) > 0.5 || Math.Abs(h - _displayHeight) > 0.5)
        {
            DisplayWidth = w;
            DisplayHeight = h;
        }
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
        var sink = _vlcSink;
        _mediaPlayer = null;
        _vlcSink = null;
        if (player == null)
            return;

        // Stopping can block and must not run on the UI thread or inside a VLC event callback.
        Task.Run(() =>
        {
            try
            {
                player.Stop();
                player.Dispose();
                sink?.Dispose();
            }
            catch (Exception e)
            {
                System.Diagnostics.Debug.WriteLine(e);
            }
        });
    }
}
