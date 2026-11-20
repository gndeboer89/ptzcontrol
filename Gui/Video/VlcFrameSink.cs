using System;
using System.Runtime.InteropServices;
using System.Text;
using LibVLCSharp.Shared;
using PtzJoystickControl.Core.Video;

namespace PtzJoystickControl.Gui.Video;

/// <summary>
/// Receives decoded video from a LibVLC <see cref="MediaPlayer"/> as plain BGRA pixels, so the picture can be shown
/// like any other image. This avoids LibVLC drawing into a native child window of its own, which fails on some
/// Windows setups ("Unable to create child window for native control host").
/// </summary>
internal sealed class VlcFrameSink : IDisposable
{
    private const int BytesPerPixel = 4;

    private readonly object _gate = new();
    private byte[] _back = Array.Empty<byte>();
    private byte[] _front = Array.Empty<byte>();
    private GCHandle _backHandle, _frontHandle;
    private int _width, _height;
    private bool _pending;
    private bool _disposed;

    // The delegates must stay referenced for as long as the player can call them.
    private readonly MediaPlayer.LibVLCVideoFormatCb _format;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanup;
    private readonly MediaPlayer.LibVLCVideoLockCb _lock;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlock;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _display;

    public VlcFrameSink()
    {
        _format = OnFormat;
        _cleanup = OnCleanup;
        _lock = OnLock;
        _unlock = OnUnlock;
        _display = OnDisplay;
    }

    /// <summary>Raised on a VLC thread when a new frame can be taken with <see cref="TryTakeFrame"/>.</summary>
    public event Action? FrameAvailable;

    /// <summary>Must be called before the player starts playing.</summary>
    public void Attach(MediaPlayer player)
    {
        player.SetVideoFormatCallbacks(_format, _cleanup);
        player.SetVideoCallbacks(_lock, _unlock, _display);
    }

    /// <summary>Copies the newest frame (tightly packed BGRA) into <paramref name="destination"/>, resizing it as needed.</summary>
    public bool TryTakeFrame(ref byte[] destination, out int width, out int height)
    {
        lock (_gate)
        {
            width = _width;
            height = _height;
            if (!_pending || _front.Length == 0)
                return false;

            if (destination.Length != _front.Length)
                destination = new byte[_front.Length];
            Buffer.BlockCopy(_front, 0, destination, 0, _front.Length);
            _pending = false;
            return true;
        }
    }

    // ---- LibVLC callbacks (VLC threads) ---------------------------------------------------------

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // "RV32" = 32 bits per pixel; in memory that is blue, green, red, unused: BGRX.
        Marshal.Copy(Encoding.ASCII.GetBytes("RV32"), 0, chroma, 4);

        lock (_gate)
        {
            if (_disposed)
                return 0;
            ReleaseBuffers();
            _width = checked((int)width);
            _height = checked((int)height);
            _back = new byte[_width * _height * BytesPerPixel];
            _front = new byte[_back.Length];
            _backHandle = GCHandle.Alloc(_back, GCHandleType.Pinned);
            _frontHandle = GCHandle.Alloc(_front, GCHandleType.Pinned);
            _pending = false;
        }
        pitches = (uint)(_width * BytesPerPixel);
        lines = (uint)_height;
        return 1; // one picture buffer
    }

    private void OnCleanup(ref IntPtr opaque)
    {
        lock (_gate)
            ReleaseBuffers();
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        lock (_gate)
        {
            if (!_backHandle.IsAllocated)
                return IntPtr.Zero;
            Marshal.WriteIntPtr(planes, _backHandle.AddrOfPinnedObject());
        }
        return IntPtr.Zero;
    }

    private void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
    }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        lock (_gate)
        {
            if (!_backHandle.IsAllocated)
                return;
            // The picture VLC just finished becomes the front frame; the old front is VLC's next target.
            (_front, _back) = (_back, _front);
            (_frontHandle, _backHandle) = (_backHandle, _frontHandle);
            _pending = true;
        }
        FrameAvailable?.Invoke();
    }

    private void ReleaseBuffers()
    {
        if (_backHandle.IsAllocated)
            _backHandle.Free();
        if (_frontHandle.IsAllocated)
            _frontHandle.Free();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            ReleaseBuffers();
        }
    }
}
