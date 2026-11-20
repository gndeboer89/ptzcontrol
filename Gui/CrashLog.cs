using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PtzJoystickControl.Gui;

/// <summary>Writes unexpected errors to a log file next to the program's settings, so they can be investigated.</summary>
public static class CrashLog
{
    private static readonly object Gate = new();

    public static string FilePath
    {
        get
        {
            var root = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".PTZJoystickControl")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PTZJoystickControl");
            return Path.Combine(root, "crash.log");
        }
    }

    public static void Write(string context, Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                TrimIfLarge();
                var text = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("  ").AppendLine(context)
                    .AppendLine(exception?.ToString() ?? "(no exception)")
                    .AppendLine()
                    .ToString();
                File.AppendAllText(FilePath, text);
            }
        }
        catch
        {
            // Logging must never cause a second failure.
        }
    }

    private static void TrimIfLarge()
    {
        var info = new FileInfo(FilePath);
        if (info.Exists && info.Length > 512 * 1024)
            File.Delete(FilePath);
    }
}
