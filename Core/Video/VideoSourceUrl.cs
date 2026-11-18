namespace PtzJoystickControl.Core.Video;

public enum VideoSourceKind
{
    None,
    Rtsp,
    Ndi,
    /// <summary>Some other URL VLC may still be able to play (http, srt, udp...).</summary>
    Other,
    Invalid,
}

public static class VideoSourceUrl
{
    private static readonly string[] OtherSchemes = { "http", "https", "srt", "udp", "rtp", "rtmp", "rtsps" };

    public static VideoSourceKind Classify(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return VideoSourceKind.None;

        url = url.Trim();
        if (url.StartsWith("ndi://", StringComparison.OrdinalIgnoreCase))
            return url.Length > "ndi://".Length ? VideoSourceKind.Ndi : VideoSourceKind.Invalid;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return VideoSourceKind.Invalid;

        if (uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrEmpty(uri.Host) ? VideoSourceKind.Invalid : VideoSourceKind.Rtsp;

        return OtherSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrEmpty(uri.Host)
            ? VideoSourceKind.Other
            : VideoSourceKind.Invalid;
    }

    /// <summary>Builds an <c>ndi://</c> URL from an NDI source name such as "STUDIO-PC (Camera 1)".</summary>
    public static string NdiUrl(string sourceName) => "ndi://" + sourceName.Trim().Replace("%", "%25");

    /// <summary>The NDI source name from an <c>ndi://Name</c> URL, or null for other kinds.</summary>
    public static string? NdiSourceName(string? url)
    {
        if (Classify(url) != VideoSourceKind.Ndi)
            return null;
        return Uri.UnescapeDataString(url!.Trim()["ndi://".Length..]);
    }

    /// <summary>Removes any user:password from the URL so it can be shown in the UI or logs.</summary>
    public static string RedactCredentials(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "";
        url = url.Trim();

        int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
            return url;

        int authorityStart = schemeEnd + 3;
        int authorityEnd = url.IndexOfAny(new[] { '/', '?', '#' }, authorityStart);
        if (authorityEnd < 0)
            authorityEnd = url.Length;

        // Passwords may contain '@', so the credentials end at the last '@' of the authority.
        int at = url.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart);
        return at < 0 ? url : url[..authorityStart] + url[(at + 1)..];
    }
}
