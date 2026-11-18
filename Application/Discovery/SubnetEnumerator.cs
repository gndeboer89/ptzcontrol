using System.Buffers.Binary;
using System.Net;

namespace PtzJoystickControl.Application.Discovery;

public static class SubnetEnumerator
{
    /// <summary>Largest network that will be scanned (/22 = 1022 hosts). Bigger networks are narrowed around the local address.</summary>
    public const int MaxScanPrefixLength = 22;

    /// <summary>All host addresses of the network of <paramref name="local"/>, excluding the local address, network and broadcast address.</summary>
    public static IEnumerable<IPAddress> Hosts(IPAddress local, int prefixLength)
    {
        if (prefixLength is < 1 or > 30)
            yield break;

        prefixLength = Math.Max(prefixLength, MaxScanPrefixLength);
        uint ip = BinaryPrimitives.ReadUInt32BigEndian(local.GetAddressBytes());
        uint mask = uint.MaxValue << (32 - prefixLength);
        uint network = ip & mask;
        uint broadcast = network | ~mask;

        for (uint host = network + 1; host < broadcast; host++)
        {
            if (host == ip)
                continue;
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, host);
            yield return new IPAddress(bytes);
        }
    }
}
