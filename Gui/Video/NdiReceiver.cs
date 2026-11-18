using System;
using System.Runtime.InteropServices;
using System.Threading;
using NewTek;
using PtzJoystickControl.Core.Video;

namespace PtzJoystickControl.Gui.Video;

/// <summary>Receives video from one NDI source on a background thread and hands out BGRA frames.</summary>
internal sealed class NdiReceiver : IDisposable
{
    private const int BytesPerPixel = 4;

    private readonly string _sourceName;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _frameGate = new();

    private byte[] _front = Array.Empty<byte>();
    private byte[] _back = Array.Empty<byte>();
    private int _frameWidth, _frameHeight;
    private bool _framePending;

    public NdiReceiver(string sourceName)
    {
        _sourceName = sourceName;
        _thread = new Thread(Run) { IsBackground = true, Name = "NDI receiver" };
    }

    /// <summary>Raised on the receiver thread when a new frame can be taken with <see cref="TryTakeFrame"/>.</summary>
    public event Action? FrameAvailable;

    /// <summary>Raised on the receiver thread with a short status text.</summary>
    public event Action<string>? StatusChanged;

    public void Start() => _thread.Start();

    /// <summary>Copies the latest frame into <paramref name="destination"/> (resized as needed) and returns its size.</summary>
    public bool TryTakeFrame(ref byte[] destination, out int width, out int height)
    {
        lock (_frameGate)
        {
            width = _frameWidth;
            height = _frameHeight;
            if (!_framePending)
                return false;

            // Swap instead of copying: the caller gets the newest frame, we keep its old buffer for reuse.
            (_front, destination) = (destination, _front);
            _framePending = false;
            return true;
        }
    }

    private void Run()
    {
        IntPtr receiver = IntPtr.Zero;
        IntPtr nameUtf8 = IntPtr.Zero;
        IntPtr recvNameUtf8 = IntPtr.Zero;
        try
        {
            if (!NdiRuntime.TryInitialize(out var error))
            {
                StatusChanged?.Invoke(error ?? "NDI is not available.");
                return;
            }

            nameUtf8 = NewTek.NDI.UTF.StringToUtf8(_sourceName);
            recvNameUtf8 = NewTek.NDI.UTF.StringToUtf8("PTZ Joystick Control");
            var settings = new NDIlib.recv_create_v3_t
            {
                source_to_connect_to = new NDIlib.source_t { p_ndi_name = nameUtf8 },
                color_format = NDIlib.recv_color_format_e.recv_color_format_BGRX_BGRA,
                bandwidth = NDIlib.recv_bandwidth_e.recv_bandwidth_highest,
                allow_video_fields = false,
                p_ndi_recv_name = recvNameUtf8,
            };

            receiver = NDIlib.recv_create_v3(ref settings);
            if (receiver == IntPtr.Zero)
            {
                StatusChanged?.Invoke("Could not create the NDI receiver.");
                return;
            }

            StatusChanged?.Invoke($"Connecting to NDI source \"{_sourceName}\"...");
            bool gotVideo = false;
            var lastVideo = DateTime.UtcNow;

            while (!_cts.IsCancellationRequested)
            {
                var video = new NDIlib.video_frame_v2_t();
                var audio = new NDIlib.audio_frame_v3_t();
                var metadata = new NDIlib.metadata_frame_t();

                var type = NDIlib.recv_capture_v3(receiver, ref video, ref audio, ref metadata, 200);
                switch (type)
                {
                    case NDIlib.frame_type_e.frame_type_video:
                        try { StoreFrame(video); }
                        finally { NDIlib.recv_free_video_v2(receiver, ref video); }
                        lastVideo = DateTime.UtcNow;
                        if (!gotVideo)
                        {
                            gotVideo = true;
                            StatusChanged?.Invoke("Playing (NDI)");
                        }
                        break;
                    case NDIlib.frame_type_e.frame_type_audio:
                        NDIlib.recv_free_audio_v3(receiver, ref audio); // audio is not played yet
                        break;
                    case NDIlib.frame_type_e.frame_type_metadata:
                        NDIlib.recv_free_metadata(receiver, ref metadata);
                        break;
                    case NDIlib.frame_type_e.frame_type_error:
                        StatusChanged?.Invoke("The NDI connection failed.");
                        return;
                }

                if (DateTime.UtcNow - lastVideo > TimeSpan.FromSeconds(5) && gotVideo)
                {
                    gotVideo = false;
                    StatusChanged?.Invoke("No video from the NDI source. Waiting...");
                }
                else if (!gotVideo && DateTime.UtcNow - lastVideo > TimeSpan.FromSeconds(8) && NDIlib.recv_get_no_connections(receiver) == 0)
                {
                    StatusChanged?.Invoke($"NDI source \"{_sourceName}\" not found on the network.");
                    lastVideo = DateTime.UtcNow;
                }
            }
        }
        catch (Exception e) when (e is not ThreadAbortException)
        {
            StatusChanged?.Invoke("NDI error: " + e.Message);
        }
        finally
        {
            if (receiver != IntPtr.Zero)
                NDIlib.recv_destroy(receiver);
            if (nameUtf8 != IntPtr.Zero) Marshal.FreeHGlobal(nameUtf8);
            if (recvNameUtf8 != IntPtr.Zero) Marshal.FreeHGlobal(recvNameUtf8);
        }
    }

    private void StoreFrame(NDIlib.video_frame_v2_t video)
    {
        if (video.p_data == IntPtr.Zero || video.xres <= 0 || video.yres <= 0)
            return;

        int rowBytes = video.xres * BytesPerPixel;
        int length = rowBytes * video.yres;

        lock (_frameGate)
        {
            // The UI has not picked up the previous frame yet: drop this one instead of building a backlog.
            if (_framePending)
                return;
            if (_back.Length != length)
                _back = new byte[length];

            // BGRX/BGRA frames have a stride that may be larger than the row.
            FrameCopy.ToManaged(video.p_data, Math.Max(video.line_stride_in_bytes, rowBytes), video.yres, rowBytes, _back);
            (_front, _back) = (_back, _front);
            _frameWidth = video.xres;
            _frameHeight = video.yres;
            _framePending = true;
        }
        FrameAvailable?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        // The loop wakes up at least every 200 ms.
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(3)))
            System.Diagnostics.Debug.WriteLine("NDI receiver thread did not stop in time");
        _cts.Dispose();
    }
}
