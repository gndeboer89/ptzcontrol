using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PtzJoystickControl.Application.Ndi;
using PtzJoystickControl.Core.Discovery;

namespace PtzJoystickControl.Application.Discovery;

public class CameraDiscoveryService : ICameraDiscoveryService
{
    private static readonly TimeSpan OnvifListenTime = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ScanReplyWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TcpConnectTimeout = TimeSpan.FromMilliseconds(600);
    private const int MaxParallelTcpConnects = 64;

    public async Task<IReadOnlyList<DiscoveredCamera>> DiscoverAsync(
        IProgress<DiscoveredCamera>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var found = new ConcurrentDictionary<IPAddress, DiscoveredCamera>();

        void Report(DiscoveredCamera camera)
        {
            var merged = found.AddOrUpdate(camera.Address, camera, (_, existing) => existing.Merge(camera));
            progress?.Report(merged);
        }

        var interfaces = GetLocalNetworks();
        var tasks = new List<Task>();
        foreach (var (local, prefix) in interfaces)
        {
            tasks.Add(SafeAsync(() => ProbeOnvifAsync(local, Report, cancellationToken)));
            tasks.Add(SafeAsync(() => ScanViscaUdpAsync(local, prefix, Report, cancellationToken)));
            tasks.Add(SafeAsync(() => ScanViscaTcpAsync(local, prefix, Report, cancellationToken)));
        }

        // NDI sources reveal a camera's address even when it does not answer our VISCA probes.
        tasks.Add(SafeAsync(() => FindNdiAsync(Report, cancellationToken)));

        await Task.WhenAll(tasks).ConfigureAwait(false);

        return found.Values.OrderBy(c => c.Address.GetAddressBytes(), ByteArrayComparer.Instance).ToList();
    }

    // A failure on one network interface must not abort discovery on the others.
    private static async Task SafeAsync(Func<Task> action)
    {
        try { await action().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            Debug.WriteLine($"Discovery error: {e.Message}");
        }
    }

    private static async Task FindNdiAsync(Action<DiscoveredCamera> report, CancellationToken ct)
    {
        try
        {
            var candidates = await Task.Run(() => NdiDiscovery.Find(OnvifListenTime, ct), ct).ConfigureAwait(false);
            foreach (var candidate in candidates)
                report(candidate);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            // NDI is optional: a missing or misbehaving runtime must never break the rest of the discovery.
            Debug.WriteLine($"NDI discovery failed: {e.Message}");
        }
    }

    private static IEnumerable<(IPAddress Address, int PrefixLength)> GetLocalNetworks()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                // Skip link-local (169.254.x.x): usually an unconfigured adapter.
                var b = address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254)
                    continue;
                yield return (address, unicast.PrefixLength);
            }
        }
    }

    private static async Task ProbeOnvifAsync(IPAddress local, Action<DiscoveredCamera> report, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(local, 0));
        DisableUdpConnectionReset(udp);
        var probe = System.Text.Encoding.UTF8.GetBytes(WsDiscovery.BuildProbe(Guid.NewGuid()));
        await udp.SendAsync(probe, new IPEndPoint(WsDiscovery.MulticastAddress, WsDiscovery.Port), ct).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(OnvifListenTime);
        try
        {
            while (true)
            {
                UdpReceiveResult result;
                try { result = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false); }
                catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused or SocketError.HostUnreachable or SocketError.NetworkUnreachable) { continue; }
                var camera = WsDiscovery.ParseProbeMatch(result.Buffer, result.Buffer.Length, result.RemoteEndPoint.Address);
                if (camera != null)
                    report(camera);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* listen time is over */ }
    }

    private static Task ScanViscaUdpAsync(IPAddress local, int prefixLength, Action<DiscoveredCamera> report, CancellationToken ct) =>
        ScanViscaUdpAsync(local, SubnetEnumerator.Hosts(local, prefixLength), ViscaProbe.UdpTargets, ScanReplyWait, report, ct);

    /// <summary>
    /// Sends a VISCA inquiry to every host on every target port and reports hosts that answer like a VISCA camera.
    /// Internal (not private) so tests can run it against fake cameras on the loopback network.
    /// </summary>
    internal static async Task ScanViscaUdpAsync(
        IPAddress local,
        IEnumerable<IPAddress> hosts,
        IReadOnlyList<ViscaProbeTarget> targets,
        TimeSpan replyWait,
        Action<DiscoveredCamera> report,
        CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(local, 0));
        DisableUdpConnectionReset(udp);

        using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receiver = Task.Run(async () =>
        {
            while (!receiveCts.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await udp.ReceiveAsync(receiveCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused or SocketError.HostUnreachable or SocketError.NetworkUnreachable)
                {
                    // Windows reports an earlier "port unreachable" (ICMP) for a host we probed as an error on the next
                    // receive. That says nothing about the other hosts, so it must not stop us listening.
                    continue;
                }
                catch (SocketException)
                {
                    return;
                }

                if (!ViscaProbe.IsViscaReply(result.Buffer, result.Buffer.Length))
                    continue;
                // The camera answers from the port we probed, which tells us which format it speaks.
                foreach (var target in targets)
                {
                    if (target.Port == result.RemoteEndPoint.Port)
                    {
                        report(ViscaProbe.ToCamera(result.RemoteEndPoint.Address, target, ViscaProbe.IsHeaderedReply(result.Buffer, result.Buffer.Length)));
                        break;
                    }
                }
            }
        }, CancellationToken.None);

        int sent = 0;
        foreach (var host in hosts)
        {
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                var packet = ViscaProbe.BuildInquiry(target.UseHeader);
                try { await udp.SendAsync(packet, new IPEndPoint(host, target.Port), ct).ConfigureAwait(false); }
                catch (SocketException) { /* host unreachable etc.; keep scanning */ }
            }
            // Pace the burst so switches and slow cameras are not overwhelmed.
            if (++sent % 32 == 0)
                await Task.Delay(5, ct).ConfigureAwait(false);
        }

        try { await Task.Delay(replyWait, ct).ConfigureAwait(false); }
        finally
        {
            receiveCts.Cancel();
            await receiver.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Windows turns ICMP "port unreachable" answers into receive errors on UDP sockets (SIO_UDP_CONNRESET).
    /// A scan provokes lots of those, so switch that behaviour off.
    /// </summary>
    private static void DisableUdpConnectionReset(UdpClient udp)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
        }
        catch (Exception e) when (e is SocketException or NotSupportedException or InvalidOperationException)
        {
            Debug.WriteLine($"Could not disable UDP connection reset: {e.Message}");
        }
    }

    private static async Task ScanViscaTcpAsync(IPAddress local, int prefixLength, Action<DiscoveredCamera> report, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(MaxParallelTcpConnects);
        var probes = new List<Task>();

        foreach (var host in SubnetEnumerator.Hosts(local, prefixLength))
        {
            foreach (var port in ViscaProbe.TcpPorts)
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                probes.Add(Task.Run(async () =>
                {
                    try
                    {
                        using var client = new TcpClient();
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(TcpConnectTimeout);
                        await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
                        report(new DiscoveredCamera(host)
                        {
                            Source = DiscoverySource.TcpPortOpen,
                            ViscaPort = port,
                            ViscaProtocol = Core.Devices.Protocol.Tcp,
                            ViscaUseHeader = false,
                        });
                    }
                    catch (Exception e) when (e is OperationCanceledException or SocketException or IOException) { }
                    finally { gate.Release(); }
                }, CancellationToken.None));
            }
        }

        await Task.WhenAll(probes).ConfigureAwait(false);
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y.AsSpan());
    }
}
