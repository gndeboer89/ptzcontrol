using System;
using PtzJoystickControl.Application.Ndi;

namespace PtzJoystickControl.Gui.Video;

/// <summary>
/// Loads the NDI runtime the user has installed (NDI Tools / NDI Runtime). We do not ship NDI itself.
/// </summary>
internal static class NdiRuntime
{
    public const string DownloadUrl = "https://ndi.video/tools/";

    private static readonly object Gate = new();
    private static bool _initialized;

    /// <summary>Initialises NDI once. Returns false with a user-readable message if that is not possible.</summary>
    public static bool TryInitialize(out string? error)
    {
        lock (Gate)
        {
            if (_initialized)
            {
                error = null;
                return true;
            }

            try
            {
                if (!NdiNative.Initialize())
                {
                    error = "NDI could not be started on this computer (unsupported CPU or the runtime failed to start).";
                    return false;
                }
                _initialized = true;
                error = null;
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                error = Describe(e);
                return false;
            }
        }
    }

    private static string Describe(Exception e)
    {
        var bits = Environment.Is64BitProcess ? "64-bit" : "32-bit";
        var tried = NdiNative.LoadDiagnostics;
        var detail = string.IsNullOrEmpty(tried) ? "" : $" Looked for: {tried}.";
        return e is BadImageFormatException
            ? $"The NDI runtime found does not match this {bits} app.{detail}"
            : $"The NDI Runtime could not be loaded by this {bits} app. Install the free NDI Tools / Runtime from {DownloadUrl} and restart the program.{detail}";
    }
}
