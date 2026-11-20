using System.Net;
using PtzJoystickControl.Core.Devices;

namespace PtzJoystickControl.Core.Discovery;

/// <summary>How a camera was found on the network.</summary>
[Flags]
public enum DiscoverySource
{
    None = 0,
    /// <summary>Answered an ONVIF WS-Discovery probe.</summary>
    Onvif = 1,
    /// <summary>Answered a VISCA probe during a subnet scan.</summary>
    ViscaScan = 2,
    /// <summary>Only a TCP port was open; not confirmed to speak VISCA.</summary>
    TcpPortOpen = 4,
    /// <summary>Announced itself as an NDI source (gives us its address, not its control port).</summary>
    Ndi = 8,
}

/// <summary>A camera (or something that looks like one) found by discovery.</summary>
public record DiscoveredCamera(IPAddress Address)
{
    public string? Name { get; init; }
    public string? Hardware { get; init; }
    public Uri? OnvifServiceUri { get; init; }

    /// <summary>NDI source name (for example "CAMERA (NDI)"), when the device sends NDI video.</summary>
    public string? NdiSourceName { get; init; }

    public DiscoverySource Source { get; init; }

    public int? ViscaPort { get; init; }
    public Protocol? ViscaProtocol { get; init; }
    /// <summary>Whether the camera expects the VISCA-over-IP header (Sony style).</summary>
    public bool? ViscaUseHeader { get; init; }

    public bool SupportsVisca => ViscaPort != null;

    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name! : $"Camera {Address}";

    /// <summary>Combines two results for the same address, keeping the most specific information.</summary>
    public DiscoveredCamera Merge(DiscoveredCamera other)
    {
        // Prefer a confirmed VISCA answer over an open TCP port.
        var viscaFrom = this;
        if (!SupportsVisca || (other.SupportsVisca && Rank(other) > Rank(this)))
            viscaFrom = other;

        return this with
        {
            Name = Name ?? other.Name,
            Hardware = Hardware ?? other.Hardware,
            OnvifServiceUri = OnvifServiceUri ?? other.OnvifServiceUri,
            NdiSourceName = NdiSourceName ?? other.NdiSourceName,
            Source = Source | other.Source,
            ViscaPort = viscaFrom.ViscaPort,
            ViscaProtocol = viscaFrom.ViscaProtocol,
            ViscaUseHeader = viscaFrom.ViscaUseHeader,
        };
    }

    private static int Rank(DiscoveredCamera c) => c.Source.HasFlag(DiscoverySource.ViscaScan) ? 2 : c.SupportsVisca ? 1 : 0;
}
