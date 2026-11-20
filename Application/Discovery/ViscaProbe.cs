using System.Buffers.Binary;
using System.Net;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Discovery;

namespace PtzJoystickControl.Application.Discovery;

/// <summary>A single VISCA probe: which port to try and whether to wrap the command in the VISCA-over-IP header.</summary>
public readonly record struct ViscaProbeTarget(int Port, bool UseHeader);

public static class ViscaProbe
{
    /// <summary>UDP ports/formats cameras commonly use: Sony style (header, 52381) and raw VISCA (1259, 5678).</summary>
    public static readonly IReadOnlyList<ViscaProbeTarget> UdpTargets = new[]
    {
        new ViscaProbeTarget(52381, true),
        new ViscaProbeTarget(1259, false),
        new ViscaProbeTarget(5678, false),
    };

    /// <summary>TCP ports used for VISCA over TCP. An open port is only a hint, not proof.</summary>
    public static readonly IReadOnlyList<int> TcpPorts = new[] { 5678, 1259 };

    // CAM_PowerInq: harmless read-only inquiry every VISCA camera answers.
    private static readonly byte[] PowerInquiry = { 0x81, 0x09, 0x04, 0x00, 0xFF };

    public static byte[] BuildInquiry(bool useHeader, uint sequence = 1)
    {
        if (!useHeader)
            return (byte[])PowerInquiry.Clone();

        var packet = new byte[8 + PowerInquiry.Length];
        packet[0] = 0x01; packet[1] = 0x10; // payload type: VISCA inquiry
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)PowerInquiry.Length);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), sequence);
        PowerInquiry.CopyTo(packet, 8);
        return packet;
    }

    /// <summary>True when the datagram looks like a VISCA reply (with or without the IP header).</summary>
    public static bool IsViscaReply(byte[] data, int length)
    {
        if (length < 3)
            return false;
        // Headerless: 9y 50 ... FF (completion) or 9y 4z ... FF / 9y 6z ... FF (ack / error)
        if ((data[0] & 0xF0) == 0x90 && data[length - 1] == 0xFF)
            return true;
        // With header: payload type 0x0111 (reply) or 0x0200 (control reply)
        return length >= 9 && ((data[0] == 0x01 && data[1] == 0x11) || (data[0] == 0x02 && data[1] == 0x01))
            && (data[8] & 0xF0) == 0x90;
    }

    /// <summary>True when the reply carries the 8-byte VISCA-over-IP header (01 11 ...), false for plain VISCA (90 50 ... FF).</summary>
    public static bool IsHeaderedReply(byte[] data, int length) =>
        length >= 9 && ((data[0] == 0x01 && data[1] == 0x11) || (data[0] == 0x02 && data[1] == 0x01));

    public static DiscoveredCamera ToCamera(IPAddress address, ViscaProbeTarget target) => new(address)
    {
        Source = DiscoverySource.ViscaScan,
        ViscaPort = target.Port,
        ViscaProtocol = Protocol.Udp,
        ViscaUseHeader = target.UseHeader,
    };

    /// <summary>
    /// Like <see cref="ToCamera(IPAddress, ViscaProbeTarget)"/>, but takes the Header setting from how the camera actually
    /// replied: a camera that answers in plain VISCA (even to a headered probe) is added with Header off.
    /// </summary>
    public static DiscoveredCamera ToCamera(IPAddress address, ViscaProbeTarget target, bool replyHadHeader) =>
        ToCamera(address, target) with { ViscaUseHeader = replyHadHeader };
}
