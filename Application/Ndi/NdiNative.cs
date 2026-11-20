using System.Runtime.InteropServices;
using System.Text;

namespace PtzJoystickControl.Application.Ndi;

/// <summary>
/// Minimal binding to the NDI runtime (Processing.NDI.Lib) that the user installs separately.
/// Layouts follow the NDI SDK headers; only the calls this program needs are declared.
/// Audio is received with the stable v2 structure (the v3 one contains extra fields).
/// NDI® is a registered trademark of Vizrt Group.
/// </summary>
public static class NdiNative
{
    private const string LibraryName = "NDILib"; // logical name, mapped to the real file by the resolver below
    private static readonly object DiagnosticsGate = new();
    private static readonly List<string> Attempts = new();

    static NdiNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(NdiNative).Assembly, ResolveLibrary);
    }

    /// <summary>Directories and files that were tried while loading the runtime, for error messages.</summary>
    public static string LoadDiagnostics
    {
        get { lock (DiagnosticsGate) return string.Join("; ", Attempts); }
    }

    /// <summary>File name of the runtime library for this process, e.g. Processing.NDI.Lib.x64.dll.</summary>
    public static string RuntimeFileName()
    {
        if (OperatingSystem.IsWindows())
            return RuntimeInformation.ProcessArchitecture == Architecture.X86 ? "Processing.NDI.Lib.x86.dll" : "Processing.NDI.Lib.x64.dll";
        if (OperatingSystem.IsMacOS())
            return "libndi.dylib";
        return "libndi.so";
    }

    /// <summary>The environment variables the NDI installers use to record where the runtime lives, newest first.</summary>
    public static readonly string[] RuntimeDirVariables =
    {
        "NDI_RUNTIME_DIR_V6", "NDI_RUNTIME_DIR_V5", "NDI_RUNTIME_DIR_V4", "NDI_RUNTIME_DIR_V3",
    };

    private static IntPtr ResolveLibrary(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName)
            return IntPtr.Zero;

        var fileName = RuntimeFileName();
        lock (DiagnosticsGate)
            Attempts.Clear();

        foreach (var directory in CandidateDirectories())
        {
            var path = Path.Combine(directory, fileName);
            var exists = File.Exists(path);
            lock (DiagnosticsGate)
                Attempts.Add($"{path} ({(exists ? "found" : "missing")})");
            if (exists && NativeLibrary.TryLoad(path, out var handle))
                return handle;
        }
        return IntPtr.Zero;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        foreach (var variable in RuntimeDirVariables)
        {
            var directory = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(directory))
                yield return directory.Trim().Trim('"');
        }
        yield return AppContext.BaseDirectory;
    }

    // ---- enums ---------------------------------------------------------------------------------

    public enum FrameType : int
    {
        None = 0,
        Video = 1,
        Audio = 2,
        Metadata = 3,
        Error = 4,
        StatusChange = 100,
    }

    public enum ColorFormat : int
    {
        BgrxBgra = 0,
    }

    public enum Bandwidth : int
    {
        MetadataOnly = -10,
        AudioOnly = 10,
        Lowest = 0,
        Highest = 100,
    }

    // ---- structures (x64: sizes 16, 24, 40, 72, 56, 24) -----------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct Source
    {
        public IntPtr Name;       // const char*
        public IntPtr UrlAddress; // const char*
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FindCreate
    {
        public byte ShowLocalSources; // bool
        public IntPtr Groups;
        public IntPtr ExtraIps;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RecvCreateV3
    {
        public Source SourceToConnectTo;
        public ColorFormat ColorFormat;
        public Bandwidth Bandwidth;
        public byte AllowVideoFields; // bool
        public IntPtr ReceiverName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoFrameV2
    {
        public int Xres;
        public int Yres;
        public int FourCC;
        public int FrameRateN;
        public int FrameRateD;
        public float PictureAspectRatio;
        public int FrameFormatType;
        public long Timecode;
        public IntPtr Data;
        public int LineStrideInBytes;
        public IntPtr Metadata;
        public long Timestamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioFrameV2
    {
        public int SampleRate;
        public int NoChannels;
        public int NoSamples;
        public long Timecode;
        public IntPtr Data;
        public int ChannelStrideInBytes;
        public IntPtr Metadata;
        public long Timestamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MetadataFrame
    {
        public int Length;
        public long Timecode;
        public IntPtr Data;
    }

    // ---- functions -----------------------------------------------------------------------------

    [DllImport(LibraryName, EntryPoint = "NDIlib_initialize", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool Initialize();

    [DllImport(LibraryName, EntryPoint = "NDIlib_find_create_v2", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FindCreate2(ref FindCreate settings);

    [DllImport(LibraryName, EntryPoint = "NDIlib_find_destroy", CallingConvention = CallingConvention.Cdecl)]
    public static extern void FindDestroy(IntPtr finder);

    [DllImport(LibraryName, EntryPoint = "NDIlib_find_wait_for_sources", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool FindWaitForSources(IntPtr finder, uint timeoutMs);

    [DllImport(LibraryName, EntryPoint = "NDIlib_find_get_current_sources", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FindGetCurrentSources(IntPtr finder, out uint count);

    [DllImport(LibraryName, EntryPoint = "NDIlib_recv_create_v3", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr RecvCreate3(ref RecvCreateV3 settings);

    [DllImport(LibraryName, EntryPoint = "NDIlib_recv_destroy", CallingConvention = CallingConvention.Cdecl)]
    public static extern void RecvDestroy(IntPtr receiver);

    [DllImport(LibraryName, EntryPoint = "NDIlib_recv_capture_v2", CallingConvention = CallingConvention.Cdecl)]
    public static extern FrameType RecvCapture2(IntPtr receiver, ref VideoFrameV2 video, ref AudioFrameV2 audio, ref MetadataFrame metadata, uint timeoutMs);

    [DllImport(LibraryName, EntryPoint = "NDIlib_recv_free_video_v2", CallingConvention = CallingConvention.Cdecl)]
    public static extern void RecvFreeVideo2(IntPtr receiver, ref VideoFrameV2 video);

    [DllImport(LibraryName, EntryPoint = "NDIlib_recv_free_audio_v2", CallingConvention = CallingConvention.Cdecl)]
    public static extern void RecvFreeAudio2(IntPtr receiver, ref AudioFrameV2 audio);

    [DllImport(LibraryName, EntryPoint = "NDIlib_recv_free_metadata", CallingConvention = CallingConvention.Cdecl)]
    public static extern void RecvFreeMetadata(IntPtr receiver, ref MetadataFrame metadata);

    [DllImport(LibraryName, EntryPoint = "NDIlib_recv_get_no_connections", CallingConvention = CallingConvention.Cdecl)]
    public static extern int RecvGetNoConnections(IntPtr receiver);

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>Allocates a zero-terminated UTF-8 copy of a string; free it with <see cref="FreeUtf8"/>.</summary>
    public static IntPtr AllocUtf8(string text) => Marshal.StringToCoTaskMemUTF8(text);

    public static void FreeUtf8(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
            Marshal.FreeCoTaskMem(pointer);
    }

    /// <summary>Reads <paramref name="count"/> sources (name and address) from the array returned by <see cref="FindGetCurrentSources"/>.</summary>
    public static List<(string Name, string? Address)> ReadSources(IntPtr sources, uint count)
    {
        var result = new List<(string, string?)>();
        if (sources == IntPtr.Zero)
            return result;

        int size = Marshal.SizeOf<Source>();
        for (uint i = 0; i < count; i++)
        {
            var source = Marshal.PtrToStructure<Source>(IntPtr.Add(sources, checked((int)i * size)));
            var name = Marshal.PtrToStringUTF8(source.Name);
            if (!string.IsNullOrWhiteSpace(name))
                result.Add((name, Marshal.PtrToStringUTF8(source.UrlAddress)));
        }
        return result;
    }

    /// <summary>Reads <paramref name="count"/> source names from the array returned by <see cref="FindGetCurrentSources"/>.</summary>
    public static List<string> ReadSourceNames(IntPtr sources, uint count)
    {
        var names = new List<string>();
        if (sources == IntPtr.Zero)
            return names;

        int size = Marshal.SizeOf<Source>();
        for (uint i = 0; i < count; i++)
        {
            var source = Marshal.PtrToStructure<Source>(IntPtr.Add(sources, checked((int)i * size)));
            var name = Marshal.PtrToStringUTF8(source.Name);
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }
        return names;
    }
}
