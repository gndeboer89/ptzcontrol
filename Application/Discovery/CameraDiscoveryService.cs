using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
        var probe = System.Text.Encoding.UTF8.GetBytes(WsDiscovery.BuildProbe(Guid.NewGuid()));
        await udp.SendAsync(probe, new IPEndPoint(WsDiscovery.MulticastAddress, WsDiscovery.Port), ct).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(OnvifListenTime);
        try
        {
            while (true)
            {
                var result = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                var camera = WsDiscovery.ParseProbeMatch(result.Buffer, result.Buffer.Length, result.RemoteEndPoint.Address);
                if (camera != null)
                    report(camera);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* listen time is over */ }
    }

    private static async Task ScanViscaUdpAsync(IPAddress local, int prefixLength, Action<DiscoveredCamera> report, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(local, 0));

        using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receiver = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var result = await udp.ReceiveAsync(receiveCts.Token).ConfigureAwait(false);
                    if (!ViscaProbe.IsViscaReply(result.Buffer, result.Buffer.Length))
                        continue;
                    // The camera answers from the port we probed, which tells us which format it speaks.
                    foreach (var target in ViscaProbe.UdpTargets)
                    {
                        if (target.Port == result.RemoteEndPoint.Port)
                        {
                            report(ViscaProbe.ToCamera(result.RemoteEndPoint.Address, target));
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
        }, CancellationToken.None);

        int sent = 0;
        foreach (var host in SubnetEnumerator.Hosts(local, prefixLength))
        {
            foreach (var target in ViscaProbe.UdpTargets)
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

        try { await Task.Delay(ScanReplyWait, ct).ConfigureAwait(false); }
        finally
        {
            receiveCts.Cancel();
            await receiver.ConfigureAwait(false);
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
