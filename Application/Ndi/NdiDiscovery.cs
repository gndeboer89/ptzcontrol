using System.Net;
using System.Net.Sockets;
using PtzJoystickControl.Core.Discovery;

namespace PtzJoystickControl.Application.Ndi;

/// <summary>Finds NDI sources and turns them into camera candidates, because an NDI source tells us the device's IP address.</summary>
public static class NdiDiscovery
{
    /// <summary>
    /// Extracts the IPv4 address from the address an NDI source announces ("192.168.1.50:5961").
    /// Returns null for IPv6, host names and anything else we cannot use.
    /// </summary>
    public static IPAddress? ParseAddress(string? ndiAddress)
    {
        if (string.IsNullOrWhiteSpace(ndiAddress))
            return null;

        var text = ndiAddress.Trim();
        if (text.StartsWith('['))
            return null; // IPv6

        int colon = text.LastIndexOf(':');
        var host = colon >= 0 ? text[..colon] : text;
        return IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork ? ip : null;
    }

    /// <summary>Turns raw NDI sources into one candidate per IP address.</summary>
    public static IReadOnlyList<DiscoveredCamera> ToCandidates(IEnumerable<(string Name, string? Address)> sources)
    {
        var byAddress = new Dictionary<IPAddress, DiscoveredCamera>();
        foreach (var (name, address) in sources)
        {
            var ip = ParseAddress(address);
            if (ip == null)
                continue;

            var candidate = new DiscoveredCamera(ip)
            {
                Name = DisplayName(name),
                NdiSourceName = name,
                Source = DiscoverySource.Ndi,
            };
            byAddress[ip] = byAddress.TryGetValue(ip, out var existing) ? existing.Merge(candidate) : candidate;
        }
        return byAddress.Values.ToList();
    }

    // "MACHINE (Source)" -> "Source"; keeps the full text when there is nothing in parentheses.
    private static string DisplayName(string ndiName)
    {
        int open = ndiName.LastIndexOf('(');
        int close = ndiName.LastIndexOf(')');
        return open >= 0 && close > open + 1 ? ndiName.Substring(open + 1, close - open - 1).Trim() : ndiName;
    }

    /// <summary>Lists NDI sources for <paramref name="listenTime"/>. Returns nothing when the NDI runtime is not installed.</summary>
    public static IReadOnlyList<DiscoveredCamera> Find(TimeSpan listenTime, CancellationToken cancellationToken)
    {
        if (!NdiRuntime.TryInitialize(out _))
            return Array.Empty<DiscoveredCamera>();

        var settings = new NdiNative.FindCreate { ShowLocalSources = 1 };
        var finder = NdiNative.FindCreate2(ref settings);
        if (finder == IntPtr.Zero)
            return Array.Empty<DiscoveredCamera>();

        try
        {
            var seen = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var end = DateTime.UtcNow + listenTime;
            while (DateTime.UtcNow < end && !cancellationToken.IsCancellationRequested)
            {
                NdiNative.FindWaitForSources(finder, 300);
                // The returned array is only valid until the next call, so copy it right away.
                var sources = NdiNative.FindGetCurrentSources(finder, out var count);
                foreach (var (name, address) in NdiNative.ReadSources(sources, count))
                    seen[name] = address;
            }
            return ToCandidates(seen.Select(kv => (kv.Key, kv.Value)));
        }
        finally
        {
            NdiNative.FindDestroy(finder);
        }
    }
}
