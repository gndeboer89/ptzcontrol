using System.Net;
using System.Net.Sockets;
using System.Text;
using PtzJoystickControl.Application.Discovery;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Discovery;
using PtzJoystickControl.Core.Video;
using Xunit;

namespace PtzJoystickControl.Tests;

public class WsDiscoveryTests
{
    private const string ProbeMatchXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <SOAP-ENV:Envelope xmlns:SOAP-ENV="http://www.w3.org/2003/05/soap-envelope"
            xmlns:wsa="http://schemas.xmlsoap.org/ws/2004/08/addressing"
            xmlns:wsdd="http://schemas.xmlsoap.org/ws/2005/04/discovery">
          <SOAP-ENV:Body>
            <wsdd:ProbeMatches>
              <wsdd:ProbeMatch>
                <wsa:EndpointReference><wsa:Address>urn:uuid:abc</wsa:Address></wsa:EndpointReference>
                <wsdd:Types>dn:NetworkVideoTransmitter</wsdd:Types>
                <wsdd:Scopes>onvif://www.onvif.org/type/video_encoder onvif://www.onvif.org/name/PTZ%20Cam%201 onvif://www.onvif.org/hardware/ABC-123 onvif://www.onvif.org/location/city/x</wsdd:Scopes>
                <wsdd:XAddrs>http://[fe80::1]/onvif/device_service http://192.168.1.50:8080/onvif/device_service</wsdd:XAddrs>
                <wsdd:MetadataVersion>1</wsdd:MetadataVersion>
              </wsdd:ProbeMatch>
            </wsdd:ProbeMatches>
          </SOAP-ENV:Body>
        </SOAP-ENV:Envelope>
        """;

    private static DiscoveredCamera? Parse(string xml, IPAddress? sender = null)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        return WsDiscovery.ParseProbeMatch(bytes, bytes.Length, sender);
    }

    [Fact]
    public void ParsesAddressNameAndHardware()
    {
        var camera = Parse(ProbeMatchXml);

        Assert.NotNull(camera);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), camera.Address);
        Assert.Equal("PTZ Cam 1", camera.Name);
        Assert.Equal("ABC-123", camera.Hardware);
        Assert.Equal(new Uri("http://192.168.1.50:8080/onvif/device_service"), camera.OnvifServiceUri);
        Assert.Equal(DiscoverySource.Onvif, camera.Source);
    }

    [Fact]
    public void FallsBackToSenderWhenNoIPv4XAddr()
    {
        var xml = ProbeMatchXml.Replace("http://192.168.1.50:8080/onvif/device_service", "http://cam.local/onvif");
        var camera = Parse(xml, IPAddress.Parse("10.0.0.7"));

        Assert.NotNull(camera);
        Assert.Equal(IPAddress.Parse("10.0.0.7"), camera.Address);
    }

    [Fact]
    public void ReturnsNullWhenNoAddressCanBeDetermined()
    {
        var xml = ProbeMatchXml.Replace("http://192.168.1.50:8080/onvif/device_service", "http://cam.local/onvif");
        Assert.Null(Parse(xml));
    }

    [Theory]
    [InlineData("not xml at all")]
    [InlineData("<a><b/></a>")]
    [InlineData("")]
    public void IgnoresGarbage(string input) => Assert.Null(Parse(input, IPAddress.Loopback));

    [Fact]
    public void RejectsDtdToAvoidXmlAttacks()
    {
        var xml = "<!DOCTYPE x [<!ENTITY e \"boom\">]>" + ProbeMatchXml;
        Assert.Null(Parse(xml, IPAddress.Loopback));
    }

    [Fact]
    public void ProbeContainsOnvifTypeAndUniqueId()
    {
        var id = Guid.NewGuid();
        var probe = WsDiscovery.BuildProbe(id);

        Assert.Contains("dn:NetworkVideoTransmitter", probe);
        Assert.Contains($"uuid:{id}", probe);
        // Must be well-formed XML.
        System.Xml.Linq.XDocument.Parse(probe);
    }
}

public class ViscaProbeTests
{
    [Fact]
    public void HeaderlessInquiryIsRawViscaCommand() =>
        Assert.Equal(new byte[] { 0x81, 0x09, 0x04, 0x00, 0xFF }, ViscaProbe.BuildInquiry(false));

    [Fact]
    public void HeaderedInquiryHasSonnyHeader()
    {
        var packet = ViscaProbe.BuildInquiry(true, 0x01020304);

        Assert.Equal(new byte[] { 0x01, 0x10, 0x00, 0x05, 0x01, 0x02, 0x03, 0x04, 0x81, 0x09, 0x04, 0x00, 0xFF }, packet);
    }

    [Theory]
    [InlineData(new byte[] { 0x90, 0x50, 0x02, 0xFF }, true)]                                                  // completion, no header
    [InlineData(new byte[] { 0x90, 0x41, 0xFF }, true)]                                                        // ack, no header
    [InlineData(new byte[] { 0x01, 0x11, 0x00, 0x03, 0, 0, 0, 1, 0x90, 0x50, 0xFF }, true)]                    // reply with header
    [InlineData(new byte[] { 0x48, 0x54, 0x54, 0x50, 0x2F, 0x31 }, false)]                                     // "HTTP/1"
    [InlineData(new byte[] { 0x90 }, false)]
    [InlineData(new byte[] { }, false)]
    public void RecognisesViscaReplies(byte[] data, bool expected) =>
        Assert.Equal(expected, ViscaProbe.IsViscaReply(data, data.Length));
}

public class SubnetEnumeratorTests
{
    [Fact]
    public void Slash24ListsAll253OtherHosts()
    {
        var hosts = SubnetEnumerator.Hosts(IPAddress.Parse("192.168.1.20"), 24).ToList();

        Assert.Equal(253, hosts.Count);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.20"), hosts);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.0"), hosts);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.255"), hosts);
        Assert.Contains(IPAddress.Parse("192.168.1.1"), hosts);
        Assert.Contains(IPAddress.Parse("192.168.1.254"), hosts);
    }

    [Fact]
    public void LargeNetworksAreLimitedToSlash22AroundLocalAddress()
    {
        var hosts = SubnetEnumerator.Hosts(IPAddress.Parse("10.1.130.5"), 8).ToList();

        Assert.Equal(1021, hosts.Count);
        Assert.All(hosts, h => Assert.InRange(h.GetAddressBytes()[2], 128, 131));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(0)]
    public void NoHostsForUnusablePrefix(int prefix) =>
        Assert.Empty(SubnetEnumerator.Hosts(IPAddress.Parse("192.168.1.20"), prefix));
}

public class DiscoveredCameraTests
{
    private static readonly IPAddress Ip = IPAddress.Parse("192.168.1.9");

    [Fact]
    public void MergeCombinesOnvifNameWithViscaPort()
    {
        var onvif = new DiscoveredCamera(Ip) { Name = "Front", Source = DiscoverySource.Onvif, OnvifServiceUri = new Uri("http://192.168.1.9/onvif") };
        var visca = ViscaProbe.ToCamera(Ip, new ViscaProbeTarget(52381, true));

        var merged = onvif.Merge(visca);

        Assert.Equal("Front", merged.Name);
        Assert.Equal(52381, merged.ViscaPort);
        Assert.Equal(Protocol.Udp, merged.ViscaProtocol);
        Assert.True(merged.ViscaUseHeader);
        Assert.Equal(DiscoverySource.Onvif | DiscoverySource.ViscaScan, merged.Source);
    }

    [Fact]
    public void ConfirmedViscaWinsOverOpenTcpPortInEitherOrder()
    {
        var tcp = new DiscoveredCamera(Ip) { Source = DiscoverySource.TcpPortOpen, ViscaPort = 5678, ViscaProtocol = Protocol.Tcp, ViscaUseHeader = false };
        var udp = ViscaProbe.ToCamera(Ip, new ViscaProbeTarget(52381, true));

        Assert.Equal(52381, tcp.Merge(udp).ViscaPort);
        Assert.Equal(52381, udp.Merge(tcp).ViscaPort);
    }

    [Fact]
    public void DisplayNameFallsBackToAddress() =>
        Assert.Equal("Camera 192.168.1.9", new DiscoveredCamera(Ip).DisplayName);
}

public class CameraDiscoveryServiceTests
{
    [Fact]
    public async Task FinishesWithoutThrowingWhenNothingIsFound()
    {
        var service = new CameraDiscoveryService();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var result = await service.DiscoverAsync();

        Assert.NotNull(result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task CancellationStopsDiscoveryQuickly()
    {
        var service = new CameraDiscoveryService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await service.DiscoverAsync(null, cts.Token);

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }
}

public class VideoSourceUrlTests
{
    [Theory]
    [InlineData(null, VideoSourceKind.None)]
    [InlineData("", VideoSourceKind.None)]
    [InlineData("   ", VideoSourceKind.None)]
    [InlineData("rtsp://192.168.1.10:554/stream1", VideoSourceKind.Rtsp)]
    [InlineData("RTSP://user:pw@cam.local/live", VideoSourceKind.Rtsp)]
    [InlineData("  rtsp://10.0.0.1/x  ", VideoSourceKind.Rtsp)]
    [InlineData("ndi://STUDIO-PC (Camera 1)", VideoSourceKind.Ndi)]
    [InlineData("ndi://", VideoSourceKind.Invalid)]
    [InlineData("http://10.0.0.1/stream.m3u8", VideoSourceKind.Other)]
    [InlineData("srt://10.0.0.1:9000", VideoSourceKind.Other)]
    [InlineData("rtsp://", VideoSourceKind.Invalid)]
    [InlineData("192.168.1.10", VideoSourceKind.Invalid)]
    [InlineData("ftp://10.0.0.1/x", VideoSourceKind.Invalid)]
    [InlineData("file:///etc/passwd", VideoSourceKind.Invalid)]
    public void ClassifiesUrls(string? url, VideoSourceKind expected) =>
        Assert.Equal(expected, VideoSourceUrl.Classify(url));

    [Theory]
    [InlineData("rtsp://admin:secret@192.168.1.10:554/stream1", "rtsp://192.168.1.10:554/stream1")]
    [InlineData("rtsp://admin:p@ss@cam/live", "rtsp://cam/live")]
    [InlineData("rtsp://192.168.1.10/stream1", "rtsp://192.168.1.10/stream1")]
    [InlineData("rtsp://cam/path@notcreds", "rtsp://cam/path@notcreds")]
    [InlineData(null, "")]
    public void RedactsCredentials(string? url, string expected) =>
        Assert.Equal(expected, VideoSourceUrl.RedactCredentials(url));

    [Fact]
    public void ExtractsNdiSourceName()
    {
        Assert.Equal("STUDIO-PC (Camera 1)", VideoSourceUrl.NdiSourceName("ndi://STUDIO-PC%20(Camera%201)"));
        Assert.Null(VideoSourceUrl.NdiSourceName("rtsp://x/y"));
    }
}

public class UpdateServiceTests
{
    private static readonly Version Current = new(0, 1, 0, 0);

    private static PtzJoystickControl.Core.Model.Update Eval(params (string, string, bool)[] releases) =>
        PtzJoystickControl.Application.Services.UpdateService.Evaluate(releases, Current);

    [Fact]
    public void NoReleasesMeansNoUpdateAndNoError()
    {
        var update = Eval();
        Assert.False(update.Available);
        Assert.False(update.Error);
    }

    [Fact]
    public void NewerReleaseIsAvailable()
    {
        var update = Eval(("v0.1.0.0", "https://x/a", false), ("v0.2.0.0", "https://x/b", false));
        Assert.True(update.Available);
        Assert.Equal("v0.2.0.0", update.Version);
        Assert.Equal(new Uri("https://x/b"), update.Uri);
    }

    [Fact]
    public void SameVersionIsNotAnUpdate() =>
        Assert.False(Eval(("v0.1.0.0", "https://x/a", false)).Available);

    [Fact]
    public void OlderUpstreamReleaseIsNotAnUpdate() =>
        Assert.False(Eval(("v0.0.2.59", "https://x/a", false)).Available);

    [Fact]
    public void DraftsAndUnparsableTagsAreIgnored()
    {
        var update = Eval(("v9.9.9.9", "https://x/draft", true), ("nightly", "https://x/n", false), ("v0.1.1.0", "https://x/ok", false));
        Assert.Equal("v0.1.1.0", update.Version);
        Assert.True(update.Available);
    }

    [Fact]
    public void ThreePartTagsWork() =>
        Assert.True(Eval(("v0.2.0", "https://x/a", false)).Available);
}

public class FrameCopyTests
{
    [Fact]
    public void RoundTripsWithPaddedStride()
    {
        const int rows = 3, rowBytes = 8, stride = 12;
        var native = System.Runtime.InteropServices.Marshal.AllocHGlobal(rows * stride);
        try
        {
            // Fill rows with row*10+col, padding with 0xEE
            var seed = new byte[rows * stride];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < stride; c++)
                    seed[r * stride + c] = c < rowBytes ? (byte)(r * 10 + c) : (byte)0xEE;
            System.Runtime.InteropServices.Marshal.Copy(seed, 0, native, seed.Length);

            var packed = new byte[rows * rowBytes];
            FrameCopy.ToManaged(native, stride, rows, rowBytes, packed);

            Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 10, 11, 12, 13, 14, 15, 16, 17, 20, 21, 22, 23, 24, 25, 26, 27 }, packed);

            // Back into a differently padded native buffer
            const int stride2 = 16;
            var native2 = System.Runtime.InteropServices.Marshal.AllocHGlobal(rows * stride2);
            try
            {
                FrameCopy.ToNative(packed, rows, rowBytes, native2, stride2);
                var back = new byte[rows * stride2];
                System.Runtime.InteropServices.Marshal.Copy(native2, back, 0, back.Length);
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < rowBytes; c++)
                        Assert.Equal(packed[r * rowBytes + c], back[r * stride2 + c]);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(native2); }
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(native); }
    }

    [Fact]
    public void TightStrideCopiesWholeFrameAtOnce()
    {
        var native = System.Runtime.InteropServices.Marshal.AllocHGlobal(8);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 0, native, 8);
            var dst = new byte[8];
            FrameCopy.ToManaged(native, 4, 2, 4, dst);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, dst);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(native); }
    }

    [Fact]
    public void RejectsBadArguments()
    {
        var buffer = new byte[16];
        Assert.Throws<ArgumentException>(() => FrameCopy.ToManaged(IntPtr.Zero, 4, 2, 4, buffer));
        var native = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => FrameCopy.ToManaged(native, 2, 2, 4, buffer)); // stride < row
            Assert.Throws<ArgumentException>(() => FrameCopy.ToManaged(native, 4, 8, 4, buffer));            // buffer too small
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(native); }
    }
}

public class NdiUrlTests
{
    [Theory]
    [InlineData("STUDIO-PC (Camera 1)")]
    [InlineData("PC (100% sure)")]
    [InlineData("Camera%20X")]
    [InlineData("Ünïcode (Cam)")]
    public void NdiUrlRoundTrips(string name)
    {
        var url = VideoSourceUrl.NdiUrl(name);

        Assert.Equal(VideoSourceKind.Ndi, VideoSourceUrl.Classify(url));
        Assert.Equal(name, VideoSourceUrl.NdiSourceName(url));
    }
}

public class ViscaUdpScanTests
{
    // A stand-in camera on the loopback network that answers a VISCA inquiry from the port it listens on.
    private static async Task<(UdpClient Server, Task Loop)> StartFakeCamera(IPAddress address, int port, Func<byte[], byte[]?> reply, CancellationToken ct)
    {
        var server = new UdpClient(new IPEndPoint(address, port));
        var loop = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var request = await server.ReceiveAsync(ct);
                    var answer = reply(request.Buffer);
                    if (answer != null)
                        await server.SendAsync(answer, request.RemoteEndPoint, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });
        await Task.CompletedTask;
        return (server, loop);
    }

    private static readonly byte[] HeaderedReply = { 0x01, 0x11, 0x00, 0x04, 0, 0, 0, 1, 0x90, 0x50, 0x02, 0xFF };
    private static readonly byte[] RawReply = { 0x90, 0x50, 0x02, 0xFF };

    private static async Task<List<DiscoveredCamera>> Scan(IEnumerable<IPAddress> hosts, params ViscaProbeTarget[] targets)
    {
        var found = new System.Collections.Concurrent.ConcurrentBag<DiscoveredCamera>();
        await CameraDiscoveryService.ScanViscaUdpAsync(IPAddress.Loopback, hosts, targets, TimeSpan.FromMilliseconds(700), found.Add, CancellationToken.None);
        return found.ToList();
    }

    [Fact]
    public async Task FindsCameraThatAnswersWithHeader()
    {
        using var cts = new CancellationTokenSource();
        var (server, loop) = await StartFakeCamera(IPAddress.Parse("127.0.0.2"), 40111, _ => HeaderedReply, cts.Token);
        try
        {
            var found = await Scan(new[] { IPAddress.Parse("127.0.0.2") }, new ViscaProbeTarget(40111, true));

            var camera = Assert.Single(found);
            Assert.Equal(IPAddress.Parse("127.0.0.2"), camera.Address);
            Assert.Equal(40111, camera.ViscaPort);
            Assert.Equal(Protocol.Udp, camera.ViscaProtocol);
            Assert.True(camera.ViscaUseHeader);
            Assert.Equal(DiscoverySource.ViscaScan, camera.Source);
        }
        finally { cts.Cancel(); server.Dispose(); await loop; }
    }

    [Fact]
    public async Task FindsCameraThatAnswersRawViscaAndIgnoresNonViscaReplies()
    {
        using var cts = new CancellationTokenSource();
        var (raw, rawLoop) = await StartFakeCamera(IPAddress.Parse("127.0.0.2"), 40112, _ => RawReply, cts.Token);
        var (web, webLoop) = await StartFakeCamera(IPAddress.Parse("127.0.0.3"), 40112, _ => System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request"), cts.Token);
        try
        {
            var found = await Scan(new[] { IPAddress.Parse("127.0.0.2"), IPAddress.Parse("127.0.0.3") }, new ViscaProbeTarget(40112, false));

            var camera = Assert.Single(found);
            Assert.Equal(IPAddress.Parse("127.0.0.2"), camera.Address);
            Assert.False(camera.ViscaUseHeader);
        }
        finally { cts.Cancel(); raw.Dispose(); web.Dispose(); await rawLoop; await webLoop; }
    }

    [Fact]
    public async Task ManyClosedHostsDoNotPreventFindingTheCamera()
    {
        using var cts = new CancellationTokenSource();
        var (server, loop) = await StartFakeCamera(IPAddress.Parse("127.0.0.200"), 40113, _ => HeaderedReply, cts.Token);
        try
        {
            // 150 loopback addresses with nothing listening before and after the camera.
            var hosts = Enumerable.Range(10, 220).Select(i => IPAddress.Parse($"127.0.0.{i}")).ToList();

            var found = await Scan(hosts, new ViscaProbeTarget(40113, true));

            Assert.Contains(found, c => c.Address.Equals(IPAddress.Parse("127.0.0.200")));
        }
        finally { cts.Cancel(); server.Dispose(); await loop; }
    }

    [Fact]
    public async Task ReportsNothingWhenNoCameraAnswers()
    {
        var found = await Scan(new[] { IPAddress.Parse("127.0.0.50") }, new ViscaProbeTarget(40114, true));
        Assert.Empty(found);
    }
}
