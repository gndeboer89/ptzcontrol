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

public class NdiNativeTests
{
    [Fact]
    public void StructsMatchTheNdiHeadersOn64Bit()
    {
        if (IntPtr.Size != 8)
            return; // sizes below are the 64-bit ones from the NDI SDK headers

        Assert.Equal(16, System.Runtime.InteropServices.Marshal.SizeOf<PtzJoystickControl.Application.Ndi.NdiNative.Source>());
        Assert.Equal(24, System.Runtime.InteropServices.Marshal.SizeOf<PtzJoystickControl.Application.Ndi.NdiNative.FindCreate>());
        Assert.Equal(40, System.Runtime.InteropServices.Marshal.SizeOf<PtzJoystickControl.Application.Ndi.NdiNative.RecvCreateV3>());
        Assert.Equal(72, System.Runtime.InteropServices.Marshal.SizeOf<PtzJoystickControl.Application.Ndi.NdiNative.VideoFrameV2>());
        Assert.Equal(56, System.Runtime.InteropServices.Marshal.SizeOf<PtzJoystickControl.Application.Ndi.NdiNative.AudioFrameV2>());
        Assert.Equal(24, System.Runtime.InteropServices.Marshal.SizeOf<PtzJoystickControl.Application.Ndi.NdiNative.MetadataFrame>());
    }

    [Fact]
    public void LooksForTheNewestRuntimeFirst() =>
        Assert.Equal("NDI_RUNTIME_DIR_V6", PtzJoystickControl.Application.Ndi.NdiNative.RuntimeDirVariables[0]);

    [Fact]
    public void RuntimeFileNameMatchesThePlatform()
    {
        var name = PtzJoystickControl.Application.Ndi.NdiNative.RuntimeFileName();
        if (OperatingSystem.IsWindows())
            Assert.Matches(@"^Processing\.NDI\.Lib\.x(64|86)\.dll$", name);
        else
            Assert.StartsWith("libndi", name);
    }

    [Fact]
    public void ReadSourceNamesHandlesNullAndEmpty()
    {
        Assert.Empty(PtzJoystickControl.Application.Ndi.NdiNative.ReadSourceNames(IntPtr.Zero, 3));
        Assert.Empty(PtzJoystickControl.Application.Ndi.NdiNative.ReadSourceNames(new IntPtr(1), 0));
    }
}

public class NdiDiscoveryTests
{
    [Theory]
    [InlineData("192.168.1.50:5961", "192.168.1.50")]
    [InlineData("10.0.0.7", "10.0.0.7")]
    [InlineData("  172.16.4.2:5962 ", "172.16.4.2")]
    public void ParsesIPv4Addresses(string input, string expected) =>
        Assert.Equal(IPAddress.Parse(expected), PtzJoystickControl.Application.Ndi.NdiDiscovery.ParseAddress(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[fe80::1]:5961")]
    [InlineData("camera.local:5961")]
    [InlineData("not an address")]
    public void IgnoresUnusableAddresses(string? input) =>
        Assert.Null(PtzJoystickControl.Application.Ndi.NdiDiscovery.ParseAddress(input));

    [Fact]
    public void BuildsOneCandidatePerAddressWithNameAndNdiSource()
    {
        var candidates = PtzJoystickControl.Application.Ndi.NdiDiscovery.ToCandidates(new (string, string?)[]
        {
            ("TENVEO-PC (PTZ Front)", "192.168.1.50:5961"),
            ("TENVEO-PC (PTZ Front - Alt)", "192.168.1.50:5962"),   // same device, second stream
            ("STUDIO (Cam 2)", "192.168.1.51:5961"),
            ("V6 (Cam 3)", "[fe80::1]:5961"),                          // IPv6: ignored
        });

        Assert.Equal(2, candidates.Count);
        var front = candidates.Single(c => c.Address.Equals(IPAddress.Parse("192.168.1.50")));
        Assert.Equal(DiscoverySource.Ndi, front.Source);
        Assert.NotNull(front.NdiSourceName);
        Assert.False(front.SupportsVisca);
        Assert.Equal("Cam 2", candidates.Single(c => c.Address.Equals(IPAddress.Parse("192.168.1.51"))).Name);
    }

    [Fact]
    public void NdiAndViscaResultsMergeIntoOneCamera()
    {
        var ip = IPAddress.Parse("192.168.1.50");
        var ndi = new DiscoveredCamera(ip) { Name = "PTZ Front", NdiSourceName = "TENVEO-PC (PTZ Front)", Source = DiscoverySource.Ndi };
        var visca = ViscaProbe.ToCamera(ip, new ViscaProbeTarget(52381, true));

        var merged = visca.Merge(ndi);

        Assert.Equal("PTZ Front", merged.Name);
        Assert.Equal("TENVEO-PC (PTZ Front)", merged.NdiSourceName);
        Assert.Equal(52381, merged.ViscaPort);
        Assert.Equal(DiscoverySource.Ndi | DiscoverySource.ViscaScan, merged.Source);
    }
}

public class CameraDiagnosticsTests
{
    private static async Task<(UdpClient Server, Task Loop)> StartFake(IPAddress address, int port, byte[] reply, CancellationToken ct)
    {
        var server = new UdpClient(new IPEndPoint(address, port));
        var loop = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var request = await server.ReceiveAsync(ct);
                    await server.SendAsync(reply, request.RemoteEndPoint, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });
        await Task.CompletedTask;
        return (server, loop);
    }

    [Fact]
    public async Task ReportsWhichPortAnswersAsVisca()
    {
        using var cts = new CancellationTokenSource();
        var address = IPAddress.Parse("127.0.0.2");
        var (server, loop) = await StartFake(address, 40131, new byte[] { 0x01, 0x11, 0, 3, 0, 0, 0, 1, 0x90, 0x50, 0xFF }, cts.Token);
        try
        {
            var lines = await CameraDiagnostics.RunAsync(
                address,
                new[] { new ViscaProbeTarget(40131, true), new ViscaProbeTarget(40132, false) },
                new[] { 40133 },
                40134,
                TimeSpan.FromMilliseconds(700),
                CancellationToken.None);

            Assert.Equal("Checking 127.0.0.2", lines[0]);
            Assert.Contains(lines, l => l.StartsWith("UDP 40131") && l.Contains("looks like VISCA"));
            Assert.Contains(lines, l => l.StartsWith("UDP 40132") && !l.Contains("looks like VISCA"));
            Assert.Contains(lines, l => l.StartsWith("TCP 40133") && l.Contains("closed"));
            Assert.Contains(lines, l => l.StartsWith("ONVIF"));
            Assert.Contains(lines, l => l.StartsWith("Result: VISCA over IP answers on UDP 40131"));
        }
        finally { cts.Cancel(); server.Dispose(); await loop; }
    }

    [Fact]
    public async Task SaysSoWhenNothingAnswers()
    {
        var lines = await CameraDiagnostics.RunAsync(
            IPAddress.Parse("127.0.0.3"),
            new[] { new ViscaProbeTarget(40141, true) },
            new[] { 40143 },
            40144,
            TimeSpan.FromMilliseconds(500),
            CancellationToken.None);

        Assert.Contains(lines, l => l.StartsWith("Result: no VISCA answer"));
    }

    [Fact]
    public async Task ShowsHexAndSourcePortOfNonViscaReply()
    {
        using var cts = new CancellationTokenSource();
        var address = IPAddress.Parse("127.0.0.4");
        var (server, loop) = await StartFake(address, 40151, System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 400"), cts.Token);
        try
        {
            var lines = await CameraDiagnostics.RunAsync(address, new[] { new ViscaProbeTarget(40151, true) }, Array.Empty<int>(), 40154, TimeSpan.FromMilliseconds(600), CancellationToken.None);

            Assert.Contains(lines, l => l.StartsWith("UDP 40151") && l.Contains("48-54-54-50") && l.Contains("not a VISCA answer"));
            Assert.Contains(lines, l => l.StartsWith("Result: no VISCA answer"));
        }
        finally { cts.Cancel(); server.Dispose(); await loop; }
    }
}

public class WindowsManifestTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PTZJoystickControl.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }

    [Fact]
    public void ManifestDeclaresSupportedWindowsVersions()
    {
        // Without a supportedOS list Windows blocks the child window VLC's video view needs:
        // "Unable to create child window for native control host".
        var manifest = File.ReadAllText(RepoFile("Gui", "app.manifest"));

        Assert.Contains("{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}", manifest); // Windows 10 / 11
        Assert.Contains("{1f676c76-80e1-4239-95bb-83d0f6d0da78}", manifest); // Windows 8.1
        System.Xml.Linq.XDocument.Parse(manifest); // well-formed
    }

    [Fact]
    public void ProjectEmbedsTheManifest() =>
        Assert.Contains("<ApplicationManifest>app.manifest</ApplicationManifest>", File.ReadAllText(RepoFile("Gui", "Gui.csproj")));
}

[Collection("Shared UDP socket")]
public class RawViscaReplyTests
{
    private static PtzJoystickControl.Application.Devices.ViscaIpDevice NewDevice() => new("test");

    private static byte[] Buffer128(params byte[] data)
    {
        var buffer = new byte[128];
        data.CopyTo(buffer, 0);
        return buffer;
    }

    [Fact]
    public void PlainAckIsAcceptedAndCountsAsAnswer()
    {
        var device = NewDevice();
        var before = device.LastReceiveTime;

        PtzJoystickControl.Core.ViscaCommands.ViscaIPCommandParser.ParseReply(device, Buffer128(0x90, 0x41, 0xFF), 3);

        Assert.True(device.Acked);
        Assert.True(device.LastReceiveTime > before);
    }

    [Fact]
    public void AckAndCompletionInOneDatagramAreBothHandled()
    {
        var device = NewDevice();

        PtzJoystickControl.Core.ViscaCommands.ViscaIPCommandParser.ParseReply(device, Buffer128(0x90, 0x41, 0xFF, 0x90, 0x51, 0xFF), 6);

        Assert.True(device.Acked);
        Assert.True(device.Completed);
    }

    [Fact]
    public void ReplyWithHeaderStillWorks()
    {
        var device = NewDevice();
        // payload type 0x0111 (reply), length 3, sequence 7, then a completion
        var packet = Buffer128(0x01, 0x11, 0x00, 0x03, 0x00, 0x00, 0x00, 0x07, 0x90, 0x51, 0xFF);

        PtzJoystickControl.Core.ViscaCommands.ViscaIPCommandParser.ParseReply(device, packet, 11);

        Assert.True(device.Completed);
        Assert.Equal(7, device.remotePacketId);
    }

    [Theory]
    [InlineData(new byte[] { 0x90, 0x41, 0xFF }, true)]
    [InlineData(new byte[] { 0x90, 0x50, 0x02, 0xFF }, true)]
    [InlineData(new byte[] { 0x01, 0x11, 0, 3, 0, 0, 0, 1, 0x90, 0x51, 0xFF }, false)]
    [InlineData(new byte[] { 0x90, 0x41 }, false)]
    public void ClassifiesPlainReplies(byte[] data, bool expected) =>
        Assert.Equal(expected, PtzJoystickControl.Core.ViscaCommands.ViscaIPCommandParser.IsRawViscaReply(Buffer128(data), 0, data.Length));

    [Fact]
    public void AnUnparsablePacketDoesNotThrowOutOfTheDevice()
    {
        var device = NewDevice();
        var garbage = Buffer128(0x13, 0x37);

        var error = Record.Exception(() => device.ParseViscaIPReply(garbage, 2));

        Assert.Null(error); // otherwise the shared receive loop would stop for every camera
    }

    [Theory]
    [InlineData(false)]  // Header box ticked in the app
    [InlineData(true)]   // Header box not ticked
    public async Task DeviceShowsConnectedWhenTheCameraAnswersInPlainVisca(bool headerOn)
    {
        // The Tenveo case: the camera accepts the command but answers without the VISCA-over-IP header.
        int port = headerOn ? 40561 : 40562;
        using var cts = new CancellationTokenSource();
        using var camera = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        var loop = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var request = await camera.ReceiveAsync(cts.Token);
                    await camera.SendAsync(new byte[] { 0x90, 0x41, 0xFF }, request.RemoteEndPoint, cts.Token);
                    await camera.SendAsync(new byte[] { 0x90, 0x51, 0xFF }, request.RemoteEndPoint, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });

        var device = new PtzJoystickControl.Application.Devices.ViscaIpDevice("t", new IPEndPoint(IPAddress.Loopback, port))
        {
            Protocol = PtzJoystickControl.Core.Devices.Protocol.Udp,
            UseHeader = headerOn,
        };
        device.BeginSocket();
        device.PanTilt(5, 5, PtzJoystickControl.Core.Devices.PanDir.Left, PtzJoystickControl.Core.Devices.TiltDir.Up);

        bool connected = false;
        for (int i = 0; i < 40 && !connected; i++)
        {
            connected = device.Connected;
            if (!connected) await Task.Delay(50);
        }

        cts.Cancel();
        device.EndSocket();
        await loop;
        Assert.True(connected, "the camera answered, so the status must be Connected");
    }
}

public class HeaderDetectionTests
{
    [Theory]
    [InlineData(new byte[] { 0x01, 0x11, 0, 3, 0, 0, 0, 1, 0x90, 0x50, 0xFF }, true)]
    [InlineData(new byte[] { 0x90, 0x50, 0x02, 0xFF }, false)]
    public void RecognisesWhetherAReplyHasTheHeader(byte[] data, bool expected) =>
        Assert.Equal(expected, ViscaProbe.IsHeaderedReply(data, data.Length));

    [Fact]
    public async Task ScanSetsHeaderOffForCameraThatAnswersPlainToAHeaderedProbe()
    {
        // What the Tenveo does: a headered probe on 52381 gets a plain 90 50 02 FF back.
        using var cts = new CancellationTokenSource();
        var address = IPAddress.Parse("127.0.0.6");
        using var camera = new UdpClient(new IPEndPoint(address, 40171));
        var loop = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var request = await camera.ReceiveAsync(cts.Token);
                    await camera.SendAsync(new byte[] { 0x90, 0x50, 0x02, 0xFF }, request.RemoteEndPoint, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });

        var found = new System.Collections.Concurrent.ConcurrentBag<DiscoveredCamera>();
        await CameraDiscoveryService.ScanViscaUdpAsync(IPAddress.Loopback, new[] { address }, new[] { new ViscaProbeTarget(40171, true) },
            TimeSpan.FromMilliseconds(700), found.Add, CancellationToken.None);
        cts.Cancel(); camera.Dispose(); await loop;

        var result = Assert.Single(found);
        Assert.Equal(40171, result.ViscaPort);
        Assert.False(result.ViscaUseHeader);
    }

    [Fact]
    public async Task ReportTellsToLeaveHeaderOffForAPlainAnswer()
    {
        using var cts = new CancellationTokenSource();
        var address = IPAddress.Parse("127.0.0.7");
        using var camera = new UdpClient(new IPEndPoint(address, 40181));
        var loop = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var request = await camera.ReceiveAsync(cts.Token);
                    await camera.SendAsync(new byte[] { 0x90, 0x50, 0x02, 0xFF }, request.RemoteEndPoint, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });

        var lines = await CameraDiagnostics.RunAsync(address, new[] { new ViscaProbeTarget(40181, true) }, Array.Empty<int>(), 40184, TimeSpan.FromMilliseconds(600), CancellationToken.None);
        cts.Cancel(); camera.Dispose(); await loop;

        Assert.Contains(lines, l => l.StartsWith("UDP 40181") && l.Contains("plain, without header"));
        Assert.Contains(lines, l => l.StartsWith("Result:") && l.Contains("leave the Header box unticked"));
    }
}

[Collection("Shared UDP socket")]
public class ConnectionStatusTests
{
    // A fake camera that answers every datagram in plain VISCA, until told to stop.
    private sealed class FakeCamera : IDisposable
    {
        private readonly UdpClient udp;
        private readonly CancellationTokenSource cts = new();
        private readonly Task loop;
        public volatile bool Answer = true;
        public int Received;

        public FakeCamera(int port)
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            loop = Task.Run(async () =>
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        var request = await udp.ReceiveAsync(cts.Token);
                        Interlocked.Increment(ref Received);
                        if (Answer)
                            await udp.SendAsync(new byte[] { 0x90, 0x50, 0x02, 0xFF }, request.RemoteEndPoint, cts.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            });
        }

        public void Dispose() { cts.Cancel(); udp.Dispose(); loop.Wait(1000); }
    }

    private static PtzJoystickControl.Application.Devices.ViscaIpDevice FastDevice(int port) => new("t", new IPEndPoint(IPAddress.Loopback, port))
    {
        Protocol = PtzJoystickControl.Core.Devices.Protocol.Udp,
        UseHeader = false,
        HeartbeatInterval = TimeSpan.FromMilliseconds(100),
        ConnectionTimeout = TimeSpan.FromMilliseconds(500),
    };

    private static async Task<bool> WaitFor(Func<bool> condition, int ms = 3000)
    {
        for (int waited = 0; waited < ms; waited += 25)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    [Fact]
    public async Task ShowsConnectedWithoutAnyCommandBeingSent()
    {
        using var camera = new FakeCamera(40571);
        var device = FastDevice(40571);

        device.BeginSocket(); // what happens when the camera is added; no joystick, no commands

        Assert.True(await WaitFor(() => device.Connected), "the camera answers the regular check, so it must show Connected");
        device.EndSocket();
    }

    [Fact]
    public async Task NotifiesWhenTheStatusChangesBothWays()
    {
        using var camera = new FakeCamera(40572);
        var device = FastDevice(40572);
        var changes = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        device.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(device.Connected)) changes.Enqueue(device.Connected); };

        device.BeginSocket();
        Assert.True(await WaitFor(() => device.Connected));

        camera.Answer = false; // camera switched off / unplugged
        Assert.True(await WaitFor(() => !device.Connected), "no replies for longer than the timeout must show Disconnected");
        Assert.True(await WaitFor(() => changes.Contains(false)), "the screen must be told about it");

        camera.Answer = true; // and back again
        Assert.True(await WaitFor(() => device.Connected));
        device.EndSocket();
    }

    [Fact]
    public async Task StaysDisconnectedWhenNothingAnswers()
    {
        var device = FastDevice(40573); // nobody listens there
        device.BeginSocket();
        await Task.Delay(700);

        Assert.False(device.Connected);
        device.EndSocket();
    }

    [Fact]
    public async Task ChangingTheAddressForgetsTheOldAnswers()
    {
        using var camera = new FakeCamera(40574);
        var device = FastDevice(40574);
        device.BeginSocket();
        Assert.True(await WaitFor(() => device.Connected));

        device.Port = 40575; // nothing there

        Assert.False(device.Connected, "a reply from the old address must not count for the new one");
        device.EndSocket();
    }

    [Fact]
    public async Task StopsAskingAfterEndSocket()
    {
        using var camera = new FakeCamera(40576);
        var device = FastDevice(40576);
        device.BeginSocket();
        Assert.True(await WaitFor(() => camera.Received > 2));

        device.EndSocket(); // for instance when the camera is removed from the list
        await Task.Delay(300);
        int before = camera.Received;
        await Task.Delay(500);

        Assert.Equal(before, camera.Received);
    }
}

[Collection("Shared UDP socket")]
public class DeviceConcurrencyTests
{
    [Fact]
    public async Task StartingStoppingAndSendingFromSeveralThreadsDoesNotCrash()
    {
        // The status check runs on a timer thread while the user edits the address or removes the camera.
        // An unhandled exception on any of those threads would end the whole program (and this test run).
        using var camera = new UdpClient(new IPEndPoint(IPAddress.Loopback, 40591));
        using var cts = new CancellationTokenSource();
        var cameraLoop = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var r = await camera.ReceiveAsync(cts.Token);
                    await camera.SendAsync(new byte[] { 0x90, 0x41, 0xFF }, r.RemoteEndPoint, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });

        var device = new PtzJoystickControl.Application.Devices.ViscaIpDevice("t", new IPEndPoint(IPAddress.Loopback, 40591))
        {
            Protocol = PtzJoystickControl.Core.Devices.Protocol.Udp,
            UseHeader = false,
            HeartbeatInterval = TimeSpan.FromMilliseconds(5),
        };
        device.BeginSocket();

        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var stop = DateTime.UtcNow + TimeSpan.FromSeconds(2);

        Task Worker(Action action) => Task.Run(() =>
        {
            while (DateTime.UtcNow < stop)
            {
                try { action(); }
                catch (Exception e) { errors.Enqueue(e); }
            }
        });

        await Task.WhenAll(
            Worker(() => device.Port = device.Port == 40591 ? 40592 : 40591),          // the user edits the address
            Worker(() => { device.EndSocket(); device.BeginSocket(); }),                // camera removed and added again
            Worker(() => device.PanTilt(5, 5, PtzJoystickControl.Core.Devices.PanDir.Left, PtzJoystickControl.Core.Devices.TiltDir.Up)),
            Worker(() => _ = device.Connected));

        device.EndSocket();
        cts.Cancel();
        await cameraLoop;

        Assert.True(errors.IsEmpty, "unexpected exception: " + errors.FirstOrDefault());
    }
}

public class MultiviewActiveCameraTests
{
    private sealed class FakeSelection : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public PtzJoystickControl.Core.Devices.ViscaDeviceBase? Selected { get; private set; }

        public void Set(PtzJoystickControl.Core.Devices.ViscaDeviceBase? camera, string property = "SelectedCamera")
        {
            Selected = camera;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(property));
        }
    }

    // Tiles use an NDI address so no video library is needed; there is no NDI runtime here, which just shows a status text.
    private static PtzJoystickControl.Application.Devices.ViscaIpDevice Camera(string name, string? videoUrl = "ndi://test (x)") =>
        new(name) { VideoUrl = videoUrl };

    private static PtzJoystickControl.Gui.ViewModels.MultiviewViewModel Create(
        IEnumerable<PtzJoystickControl.Core.Devices.ViscaIPDeviceBase> cameras, FakeSelection selection) =>
        new(cameras, _ => { }, () => selection.Selected, selection);

    [Fact]
    public void WithoutSelectionAllPicturesAreEqual()
    {
        var selection = new FakeSelection();
        using var multiview = Create(new[] { Camera("A"), Camera("B") }, selection);

        Assert.True(multiview.IsGridMode);
        Assert.False(multiview.IsFocusMode);
        Assert.Null(multiview.ActiveTile);
        Assert.Equal(2, multiview.OtherTiles.Count);
        Assert.Equal("No camera selected for the joystick.", multiview.ActiveDescription);
    }

    [Fact]
    public void TheSelectedCameraBecomesTheLargeOne()
    {
        var a = Camera("A"); var b = Camera("B"); var c = Camera("C");
        var selection = new FakeSelection();
        using var multiview = Create(new[] { a, b, c }, selection);

        selection.Set(b);

        Assert.True(multiview.IsFocusMode);
        Assert.Equal("B", multiview.ActiveTile?.CameraName);
        Assert.Equal(new[] { "A", "C" }, multiview.OtherTiles.Select(t => t.CameraName));
        Assert.Equal("Controlling: B", multiview.ActiveDescription);
        Assert.Equal(new[] { false, true, false }, multiview.Tiles.Select(t => t.IsActive));
    }

    [Fact]
    public void ChangingTheSelectionMovesTheHighlight()
    {
        var a = Camera("A"); var b = Camera("B");
        var selection = new FakeSelection();
        using var multiview = Create(new[] { a, b }, selection);

        selection.Set(a);
        selection.Set(b); // for instance a button on the controller

        Assert.Equal("B", multiview.ActiveTile?.CameraName);
        Assert.Equal(new[] { "A" }, multiview.OtherTiles.Select(t => t.CameraName));
        Assert.False(multiview.Tiles[0].IsActive);
        Assert.True(multiview.Tiles[1].IsActive);

        selection.Set(null); // controller unplugged

        Assert.True(multiview.IsGridMode);
        Assert.All(multiview.Tiles, t => Assert.False(t.IsActive));
    }

    [Fact]
    public void TheUserCanPreferTheEqualGrid()
    {
        var a = Camera("A");
        var selection = new FakeSelection();
        using var multiview = Create(new[] { a, Camera("B") }, selection);
        selection.Set(a);

        multiview.FocusLayout = false;

        Assert.True(multiview.IsGridMode);
        Assert.False(multiview.IsFocusMode);
        Assert.True(multiview.Tiles[0].IsActive); // still marked, only the layout is equal
    }

    [Fact]
    public void ASelectedCameraWithoutVideoIsReportedButHasNoLargePicture()
    {
        var withVideo = Camera("A");
        var noVideo = Camera("NoVideo", videoUrl: null);
        var selection = new FakeSelection();
        using var multiview = Create(new[] { withVideo, noVideo }, selection);

        selection.Set(noVideo);

        Assert.Single(multiview.Tiles);
        Assert.True(multiview.IsGridMode);
        Assert.Equal("Controlling: NoVideo (no video address)", multiview.ActiveDescription);
    }

    [Fact]
    public void UnrelatedPropertyChangesAreIgnoredAndDisposeStopsListening()
    {
        var a = Camera("A");
        var selection = new FakeSelection();
        var multiview = Create(new[] { a }, selection);

        selection.Set(a, property: "SomethingElse");
        Assert.Null(multiview.ActiveTile);

        selection.Set(a);
        Assert.NotNull(multiview.ActiveTile);

        multiview.Dispose();
        selection.Set(null);

        Assert.NotNull(multiview.ActiveTile); // no longer follows after the window was closed
    }
}


public class UdpSocketTests
{
    [Theory]
    [InlineData(System.Net.Sockets.SocketError.ConnectionReset)]
    [InlineData(System.Net.Sockets.SocketError.ConnectionRefused)]
    [InlineData(System.Net.Sockets.SocketError.HostUnreachable)]
    [InlineData(System.Net.Sockets.SocketError.NetworkUnreachable)]
    public void ErrorsAboutOneUnreachableCameraDoNotStopTheSharedSocket(System.Net.Sockets.SocketError error) =>
        Assert.True(PtzJoystickControl.Core.Utils.UdpSocket.IsHarmlessReceiveError(error));

    [Theory]
    [InlineData(System.Net.Sockets.SocketError.OperationAborted)]
    [InlineData(System.Net.Sockets.SocketError.Shutdown)]
    [InlineData(System.Net.Sockets.SocketError.AccessDenied)]
    public void OtherErrorsStillCloseTheSocket(System.Net.Sockets.SocketError error) =>
        Assert.False(PtzJoystickControl.Core.Utils.UdpSocket.IsHarmlessReceiveError(error));
}

public class SelectCameraStepTests
{
    // Just enough of a gamepad for the select-camera command.
    private sealed class FakeGamepad : PtzJoystickControl.Core.Devices.IGamepad
    {
        private PtzJoystickControl.Core.Devices.ViscaDeviceBase? selected;
        public string Id => "fake";
        public string Name => "fake";
        public bool IsActivated { get; set; }
        public bool IsConnected { get; set; }
        public System.Collections.ObjectModel.ObservableCollection<PtzJoystickControl.Core.Devices.ViscaDeviceBase>? Cameras { get; set; } = new();
        public IReadOnlyList<PtzJoystickControl.Core.Commands.ICommand> Commands => Array.Empty<PtzJoystickControl.Core.Commands.ICommand>();
        public bool DetectInput { get; set; }
        public IEnumerable<PtzJoystickControl.Core.Devices.IInput> Inputs => Array.Empty<PtzJoystickControl.Core.Devices.IInput>();
        public PtzJoystickControl.Core.Devices.ViscaDeviceBase? SelectedCamera
        {
            get => selected;
            set { selected = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SelectedCamera))); }
        }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public event System.ComponentModel.PropertyChangedEventHandler? PersistentPropertyChanged { add { } remove { } }
        public void Acquire() { }
        public void Unacquire() { }
        public void Update() { }
    }

    private static (FakeGamepad Gamepad, PtzJoystickControl.Application.Commands.SelectCameraCommand Command, PtzJoystickControl.Application.Devices.ViscaIpDevice[] Cameras) Create(int count)
    {
        var gamepad = new FakeGamepad();
        var cameras = Enumerable.Range(1, count).Select(i => new PtzJoystickControl.Application.Devices.ViscaIpDevice($"Cam {i}")).ToArray();
        foreach (var camera in cameras)
            gamepad.Cameras!.Add(camera);
        return (gamepad, new PtzJoystickControl.Application.Commands.SelectCameraCommand(gamepad), cameras);
    }

    private const int Next = PtzJoystickControl.Application.Commands.SelectCameraCommand.NextCameraValue;
    private const int Previous = PtzJoystickControl.Application.Commands.SelectCameraCommand.PreviousCameraValue;

    [Theory]
    [InlineData(3, -1, +1, 0)]   // nothing selected, up: the first
    [InlineData(3, -1, -1, 2)]   // nothing selected, down: the last
    [InlineData(3, 0, +1, 1)]
    [InlineData(3, 1, +1, 2)]
    [InlineData(3, 2, +1, 0)]    // after the last comes the first
    [InlineData(3, 2, -1, 1)]
    [InlineData(3, 0, -1, 2)]    // before the first comes the last
    [InlineData(1, 0, +1, 0)]    // a single camera stays selected
    [InlineData(1, 0, -1, 0)]
    [InlineData(0, -1, +1, -1)]  // no cameras at all
    [InlineData(3, 7, +1, 0)]    // selected camera no longer in the list
    public void SteppedIndexWrapsAround(int count, int current, int step, int expected) =>
        Assert.Equal(expected, PtzJoystickControl.Application.Commands.SelectCameraCommand.SteppedIndex(count, current, step));

    [Fact]
    public void NextCyclesUpwardsAndWraps()
    {
        var (gamepad, command, cameras) = Create(3);

        var seen = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            command.Execute(Next);
            seen.Add(gamepad.SelectedCamera!.Name);
        }

        Assert.Equal(new[] { "Cam 1", "Cam 2", "Cam 3", "Cam 1" }, seen);
    }

    [Fact]
    public void PreviousCyclesDownwardsAndWraps()
    {
        var (gamepad, command, _) = Create(3);

        var seen = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            command.Execute(Previous);
            seen.Add(gamepad.SelectedCamera!.Name);
        }

        Assert.Equal(new[] { "Cam 3", "Cam 2", "Cam 1", "Cam 3" }, seen);
    }

    [Fact]
    public void StepsStartFromTheCameraThatWasChosenByHand()
    {
        var (gamepad, command, cameras) = Create(4);
        command.Execute(2); // a fixed camera, as before: Cam 3

        command.Execute(Next);
        Assert.Equal("Cam 4", gamepad.SelectedCamera!.Name);

        command.Execute(2);
        command.Execute(Previous);
        Assert.Equal("Cam 2", gamepad.SelectedCamera!.Name);
    }

    [Fact]
    public void WithoutCamerasStepsDoNothing()
    {
        var (gamepad, command, _) = Create(0);

        command.Execute(Next);
        command.Execute(Previous);

        Assert.Null(gamepad.SelectedCamera);
    }

    [Fact]
    public void OptionsKeepCameraPositionsAndAddTheTwoStepOptionsAtTheEnd()
    {
        var (_, command, _) = Create(3);

        var options = command.Options.ToList();

        Assert.Equal(5, options.Count);
        // positions in the list must equal camera positions: other code looks cameras up by position
        Assert.Equal(new[] { 0, 1, 2 }, options.Take(3).Select(o => o.Value));
        Assert.Equal(new[] { "Cam 1", "Cam 2", "Cam 3" }, options.Take(3).Select(o => o.Name));
        Assert.Equal("Next camera (ascending)", options[3].Name);
        Assert.Equal(Next, options[3].Value);
        Assert.Equal("Previous camera (descending)", options[4].Name);
        Assert.Equal(Previous, options[4].Value);
    }

    [Fact]
    public void TheStepOptionsAlsoExistWhenNoCameraHasBeenAddedYet()
    {
        var (_, command, _) = Create(0);

        Assert.Equal(new[] { Next, Previous }, command.Options.Select(o => o.Value));
    }

    [Fact]
    public void TheOptionObjectCanBeExecutedDirectly()
    {
        // This is what a button does: it executes the option chosen in the list.
        var (gamepad, command, _) = Create(2);
        var next = command.Options.First(o => o.Value == Next);

        command.Execute(next);

        Assert.Equal("Cam 1", gamepad.SelectedCamera!.Name);
    }
}

public class SelectCameraButtonTests
{
    private sealed class Gamepad : PtzJoystickControl.Core.Devices.IGamepad
    {
        private PtzJoystickControl.Core.Devices.ViscaDeviceBase? selected;
        public string Id => "fake";
        public string Name => "fake";
        public bool IsActivated { get; set; }
        public bool IsConnected { get; set; }
        public System.Collections.ObjectModel.ObservableCollection<PtzJoystickControl.Core.Devices.ViscaDeviceBase>? Cameras { get; set; } = new();
        public IReadOnlyList<PtzJoystickControl.Core.Commands.ICommand> Commands => Array.Empty<PtzJoystickControl.Core.Commands.ICommand>();
        public bool DetectInput { get; set; }
        public IEnumerable<PtzJoystickControl.Core.Devices.IInput> Inputs => Array.Empty<PtzJoystickControl.Core.Devices.IInput>();
        public PtzJoystickControl.Core.Devices.ViscaDeviceBase? SelectedCamera
        {
            get => selected;
            set { selected = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SelectedCamera))); }
        }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public event System.ComponentModel.PropertyChangedEventHandler? PersistentPropertyChanged { add { } remove { } }
        public void Acquire() { }
        public void Unacquire() { }
        public void Update() { }
    }

    private static (Gamepad Gamepad, PtzJoystickControl.Application.Devices.Input Button, PtzJoystickControl.Application.Devices.ViscaIpDevice[] Cameras) Setup(int stepValue, int cameraCount = 3)
    {
        var gamepad = new Gamepad();
        var cameras = Enumerable.Range(1, cameraCount).Select(i => new PtzJoystickControl.Application.Devices.ViscaIpDevice($"Cam {i}")).ToArray();
        foreach (var camera in cameras)
            gamepad.Cameras!.Add(camera);

        var commands = new PtzJoystickControl.Application.Services.CommandsService().GetCommandsForGamepad(gamepad).ToList();
        var button = new PtzJoystickControl.Application.Devices.Input("b1", "Button 1", PtzJoystickControl.Core.Devices.InputType.Button, commands);
        button.SelectedCommand = commands.OfType<PtzJoystickControl.Application.Commands.SelectCameraCommand>().Single();
        button.CommandValue = button.Values!.First(v => v.Value == stepValue);
        return (gamepad, button, cameras);
    }

    private static void Press(PtzJoystickControl.Application.Devices.Input button)
    {
        button.InputValue = 1;
        button.InputValue = 0;
    }

    [Fact]
    public void EachPressOfTheNextButtonMovesOneCameraUp()
    {
        var (gamepad, button, _) = Setup(PtzJoystickControl.Application.Commands.SelectCameraCommand.NextCameraValue);

        var seen = new List<string>();
        for (int i = 0; i < 4; i++) { Press(button); seen.Add(gamepad.SelectedCamera!.Name); }

        Assert.Equal(new[] { "Cam 1", "Cam 2", "Cam 3", "Cam 1" }, seen);
    }

    [Fact]
    public void EachPressOfThePreviousButtonMovesOneCameraDown()
    {
        var (gamepad, button, _) = Setup(PtzJoystickControl.Application.Commands.SelectCameraCommand.PreviousCameraValue);

        var seen = new List<string>();
        for (int i = 0; i < 4; i++) { Press(button); seen.Add(gamepad.SelectedCamera!.Name); }

        Assert.Equal(new[] { "Cam 3", "Cam 2", "Cam 1", "Cam 3" }, seen);
    }

    [Fact]
    public void HoldingTheButtonStepsOnlyOnce()
    {
        var (gamepad, button, _) = Setup(PtzJoystickControl.Application.Commands.SelectCameraCommand.NextCameraValue);

        button.InputValue = 1;
        button.InputValue = 1;
        button.InputValue = 1; // still held
        Assert.Equal("Cam 1", gamepad.SelectedCamera!.Name);

        button.InputValue = 0;
        button.InputValue = 1;
        Assert.Equal("Cam 2", gamepad.SelectedCamera!.Name);
    }

    [Fact]
    public void ANextAndAPreviousButtonCanWorkTogether()
    {
        var (gamepad, next, _) = Setup(PtzJoystickControl.Application.Commands.SelectCameraCommand.NextCameraValue);
        var commands = new PtzJoystickControl.Application.Services.CommandsService().GetCommandsForGamepad(gamepad).ToList();
        var previous = new PtzJoystickControl.Application.Devices.Input("b2", "Button 2", PtzJoystickControl.Core.Devices.InputType.Button, commands);
        previous.SelectedCommand = commands.OfType<PtzJoystickControl.Application.Commands.SelectCameraCommand>().Single();
        previous.CommandValue = previous.Values!.First(v => v.Value == PtzJoystickControl.Application.Commands.SelectCameraCommand.PreviousCameraValue);

        Press(next); Press(next); Press(next);   // Cam 3
        Press(previous);                          // Cam 2

        Assert.Equal("Cam 2", gamepad.SelectedCamera!.Name);
    }

    [Fact]
    public void TheChoiceSurvivesRenamingAndRemovingCameras()
    {
        var (gamepad, button, cameras) = Setup(PtzJoystickControl.Application.Commands.SelectCameraCommand.NextCameraValue);

        cameras[1].Name = "Stage left";   // used to reset any option with a negative value
        gamepad.Cameras!.Remove(cameras[0]);

        Assert.NotNull(button.CommandValue);
        Assert.Equal(PtzJoystickControl.Application.Commands.SelectCameraCommand.NextCameraValue, button.CommandValue!.Value);
        Press(button);
        Assert.Equal("Stage left", gamepad.SelectedCamera!.Name);
    }

    [Fact]
    public void AFixedCameraChoiceStillMovesWhenAnEarlierCameraIsRemoved()
    {
        var (gamepad, button, cameras) = Setup(stepValue: 2); // "Cam 3", as before this feature

        gamepad.Cameras!.Remove(cameras[0]); // Cam 3 is now at position 1

        Assert.Equal(1, button.CommandValue!.Value);
        Press(button);
        Assert.Equal("Cam 3", gamepad.SelectedCamera!.Name);
    }

    [Theory]
    [InlineData(-1, "Next camera (ascending)")]
    [InlineData(-2, "Previous camera (descending)")]
    [InlineData(0, "Cam 1")]
    public void TheChoiceIsSavedAndLoadedAgain(int value, string name)
    {
        var saved = System.Text.Json.JsonSerializer.Serialize(new PtzJoystickControl.Core.Model.CommandValueOption(name, value));

        var loaded = System.Text.Json.JsonSerializer.Deserialize<PtzJoystickControl.Core.Model.CommandValueOption>(saved);

        Assert.NotNull(loaded);
        Assert.Equal(value, loaded!.Value);
        Assert.Equal(name, loaded.Name);
    }
}

public class PresetWireTests
{
    private sealed class Gamepad : PtzJoystickControl.Core.Devices.IGamepad
    {
        private PtzJoystickControl.Core.Devices.ViscaDeviceBase? selected;
        public string Id => "fake";
        public string Name => "fake";
        public bool IsActivated { get; set; }
        public bool IsConnected { get; set; }
        public System.Collections.ObjectModel.ObservableCollection<PtzJoystickControl.Core.Devices.ViscaDeviceBase>? Cameras { get; set; } = new();
        public IReadOnlyList<PtzJoystickControl.Core.Commands.ICommand> Commands => Array.Empty<PtzJoystickControl.Core.Commands.ICommand>();
        public bool DetectInput { get; set; }
        public IEnumerable<PtzJoystickControl.Core.Devices.IInput> Inputs => Array.Empty<PtzJoystickControl.Core.Devices.IInput>();
        public PtzJoystickControl.Core.Devices.ViscaDeviceBase? SelectedCamera
        {
            get => selected;
            set { selected = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SelectedCamera))); }
        }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public event System.ComponentModel.PropertyChangedEventHandler? PersistentPropertyChanged { add { } remove { } }
        public void Acquire() { }
        public void Unacquire() { }
        public void Update() { }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PresetsGoOverTheWireAsSetAndRecall(bool header)
    {
        int port = header ? 40601 : 40602;
        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var cts = new CancellationTokenSource();
        using var camera = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        var loop = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var r = await camera.ReceiveAsync(cts.Token);
                    received.Enqueue(BitConverter.ToString(r.Buffer));
                    await camera.SendAsync(new byte[] { 0x90, 0x41, 0xFF }, r.RemoteEndPoint, cts.Token);
                    await camera.SendAsync(new byte[] { 0x90, 0x51, 0xFF }, r.RemoteEndPoint, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });

        var device = new PtzJoystickControl.Application.Devices.ViscaIpDevice("t", new IPEndPoint(IPAddress.Loopback, port))
        {
            Protocol = PtzJoystickControl.Core.Devices.Protocol.Udp,
            UseHeader = header,
            HeartbeatInterval = TimeSpan.FromHours(1), // keep the heartbeat out of the way: only the preset commands are looked at
        };
        device.BeginSocket();
        await Task.Delay(300);
        while (received.TryDequeue(out _)) { } // drop the first check

        var gamepad = new Gamepad();
        gamepad.Cameras!.Add(device);
        gamepad.SelectedCamera = device;
        var command = new PtzJoystickControl.Application.Commands.PresetCommand(gamepad);
        var setThree = command.Options.First(o => o.Name == "Set - 3");
        var recallThree = command.Options.First(o => o.Name == "Recall - 3");

        command.Execute(setThree);
        await Task.Delay(300);
        command.Execute(recallThree);
        await Task.Delay(300);

        cts.Cancel(); device.EndSocket(); await loop;

        var sent = received.ToArray();
        string payload(string s) => s.Contains("81-01-04-3F") ? s[s.IndexOf("81-01-04-3F")..] : s;
        // preset 3 is VISCA number 2; Set = 01, Recall = 02
        Assert.Contains(sent, s => payload(s) == "81-01-04-3F-01-02-FF");
        Assert.Contains(sent, s => payload(s) == "81-01-04-3F-02-02-FF");
        device.EndSocket();
    }
}
