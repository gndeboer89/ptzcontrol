using System;
using System.Runtime.InteropServices;
using System.Threading;
using PtzJoystickControl.Application.Ndi;
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

            nameUtf8 = NdiNative.AllocUtf8(_sourceName);
            recvNameUtf8 = NdiNative.AllocUtf8("PTZ Joystick Control");
            var settings = new NdiNative.RecvCreateV3
            {
                SourceToConnectTo = new NdiNative.Source { Name = nameUtf8 },
                ColorFormat = NdiNative.ColorFormat.BgrxBgra,
                Bandwidth = NdiNative.Bandwidth.Highest,
                AllowVideoFields = 0,
                ReceiverName = recvNameUtf8,
            };

            receiver = NdiNative.RecvCreate3(ref settings);
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
                var video = new NdiNative.VideoFrameV2();
                var audio = new NdiNative.AudioFrameV2();
                var metadata = new NdiNative.MetadataFrame();

                var type = NdiNative.RecvCapture2(receiver, ref video, ref audio, ref metadata, 200);
                switch (type)
                {
                    case NdiNative.FrameType.Video:
                        try { StoreFrame(video); }
                        finally { NdiNative.RecvFreeVideo2(receiver, ref video); }
                        lastVideo = DateTime.UtcNow;
                        if (!gotVideo)
                        {
                            gotVideo = true;
                            StatusChanged?.Invoke("Playing (NDI)");
                        }
                        break;
                    case NdiNative.FrameType.Audio:
                        NdiNative.RecvFreeAudio2(receiver, ref audio); // audio is not played yet
                        break;
                    case NdiNative.FrameType.Metadata:
                        NdiNative.RecvFreeMetadata(receiver, ref metadata);
                        break;
                    case NdiNative.FrameType.Error:
                        StatusChanged?.Invoke("The NDI connection failed.");
                        return;
                }

                if (DateTime.UtcNow - lastVideo > TimeSpan.FromSeconds(5) && gotVideo)
                {
                    gotVideo = false;
                    StatusChanged?.Invoke("No video from the NDI source. Waiting...");
                }
                else if (!gotVideo && DateTime.UtcNow - lastVideo > TimeSpan.FromSeconds(8) && NdiNative.RecvGetNoConnections(receiver) == 0)
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
                NdiNative.RecvDestroy(receiver);
            NdiNative.FreeUtf8(nameUtf8);
            NdiNative.FreeUtf8(recvNameUtf8);
        }
    }

    private void StoreFrame(NdiNative.VideoFrameV2 video)
    {
        if (video.Data == IntPtr.Zero || video.Xres <= 0 || video.Yres <= 0)
            return;

        int rowBytes = video.Xres * BytesPerPixel;
        int length = rowBytes * video.Yres;

        lock (_frameGate)
        {
            // The UI has not picked up the previous frame yet: drop this one instead of building a backlog.
            if (_framePending)
                return;
            if (_back.Length != length)
                _back = new byte[length];

            // BGRX/BGRA frames have a stride that may be larger than the row.
            FrameCopy.ToManaged(video.Data, Math.Max(video.LineStrideInBytes, rowBytes), video.Yres, rowBytes, _back);
            (_front, _back) = (_back, _front);
            _frameWidth = video.Xres;
            _frameHeight = video.Yres;
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
