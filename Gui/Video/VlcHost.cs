using System;
using LibVLCSharp.Shared;

namespace PtzJoystickControl.Gui.Video;

/// <summary>One shared LibVLC instance for the whole app; each stream gets its own MediaPlayer.</summary>
internal static class VlcHost
{
    private static readonly object Gate = new();
    private static LibVLC? _instance;

    /// <summary>Throws when the native VLC libraries cannot be loaded.</summary>
    public static LibVLC Instance
    {
        get
        {
            lock (Gate)
            {
                if (_instance == null)
                {
                    LibVLCSharp.Shared.Core.Initialize();
                    _instance = new LibVLC("--no-osd", "--no-video-title-show");
                }
                return _instance;
            }
        }
    }
}
