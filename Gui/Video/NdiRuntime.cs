using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using NewTek;

namespace PtzJoystickControl.Gui.Video;

/// <summary>
/// Loads the NDI runtime the user has installed (NDI Tools / NDI Runtime). We do not ship NDI itself.
/// </summary>
internal static class NdiRuntime
{
    public const string DownloadUrl = "https://ndi.video/tools/";

    private static readonly object Gate = new();
    private static bool _resolverInstalled;
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
                InstallResolver();
                if (!NDIlib.initialize())
                {
                    error = "NDI could not be started on this computer (unsupported CPU or runtime failed to start).";
                    return false;
                }
                _initialized = true;
                error = null;
                return true;
            }
            // Without the runtime the wrapper's static initialiser throws, wrapped in a TypeInitializationException.
            catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or TypeInitializationException)
            {
                error = $"The NDI Runtime is not installed (or does not match this {(Environment.Is64BitProcess ? "64-bit" : "32-bit")} app). Install the free NDI Tools / Runtime from {DownloadUrl}";
                return false;
            }
        }
    }

    // The NDI installers record where they live in NDI_RUNTIME_DIR_V* rather than reliably adding it to PATH.
    private static void InstallResolver()
    {
        if (_resolverInstalled)
            return;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(NDIlib).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // A resolver was already registered for the assembly; fall back to default probing.
        }
        _resolverInstalled = true;
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Contains("Processing.NDI.Lib", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        var fileName = libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? libraryName : libraryName + ".dll";
        foreach (var variable in new[] { "NDI_RUNTIME_DIR_V6", "NDI_RUNTIME_DIR_V5", "NDI_RUNTIME_DIR_V4", "NDI_RUNTIME_DIR_V3" })
        {
            var directory = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(directory))
                continue;
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                return handle;
        }
        return IntPtr.Zero; // default probing (app folder, PATH)
    }
}
