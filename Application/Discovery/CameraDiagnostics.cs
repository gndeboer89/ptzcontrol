using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace PtzJoystickControl.Application.Discovery;

/// <summary>
/// Asks one specific address what it answers to, and reports it as plain lines of text.
/// Meant for the case "my camera is not found": the report shows which protocol and port it does speak.
/// </summary>
public static class CameraDiagnostics
{
    /// <summary>TCP ports worth knowing about: VISCA (5678, 1259), web page (80), RTSP (554).</summary>
    private static readonly int[] ExtraTcpPorts = { 80, 554 };

    public static Task<IReadOnlyList<string>> RunAsync(IPAddress address, CancellationToken cancellationToken = default) =>
        RunAsync(
            address,
            ViscaProbe.UdpTargets,
            ViscaProbe.TcpPorts.Concat(ExtraTcpPorts).Distinct().ToList(),
            WsDiscovery.Port,
            TimeSpan.FromSeconds(1.5),
            cancellationToken);

    /// <summary>Internal so tests can point it at fake cameras on other ports.</summary>
    internal static async Task<IReadOnlyList<string>> RunAsync(
        IPAddress address,
        IReadOnlyList<ViscaProbeTarget> udpTargets,
        IReadOnlyList<int> tcpPorts,
        int onvifPort,
        TimeSpan replyWait,
        CancellationToken ct)
    {
        var pingTask = PingAsync(address, ct);
        var udpTasks = udpTargets.Select(t => ProbeUdpAsync(address, t, replyWait, ct)).ToList();
        var tcpTasks = tcpPorts.Select(p => ProbeTcpAsync(address, p, ct)).ToList();
        var onvifTask = ProbeOnvifAsync(address, onvifPort, replyWait, ct);

        await Task.WhenAll(udpTasks.Cast<Task>().Concat(tcpTasks).Concat(new Task[] { pingTask, onvifTask })).ConfigureAwait(false);

        var lines = new List<string> { $"Checking {address}" };
        lines.Add(pingTask.Result);
        foreach (var udp in udpTasks) lines.Add(udp.Result.Line);
        foreach (var tcp in tcpTasks) lines.Add(tcp.Result);
        lines.Add(onvifTask.Result);

        var visca = udpTasks.Where(t => t.Result.IsVisca).Select(t => t.Result.Target).ToList();
        lines.Add("");
        bool plain = udpTasks.Any(t => t.Result.IsVisca && t.Result.PlainReply);
        lines.Add(visca.Count > 0
            ? "Result: VISCA over IP answers on " + string.Join(", ", visca.Select(t => $"UDP {t.Port}")) + ". Use that port when adding the camera." +
              (plain ? " The camera answers in plain VISCA (without the header): leave the Header box unticked." : "")
            : "Result: no VISCA answer on UDP. Check in the camera's web page that VISCA over IP is enabled and which port it uses, " +
              "and that Windows Firewall does not block this program (Windows Security > Firewall > Allow an app).");
        return lines;
    }

    private static async Task<string> PingAsync(IPAddress address, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, TimeSpan.FromSeconds(1.5), cancellationToken: ct).ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? $"Ping: reply in {reply.RoundtripTime} ms"
                : $"Ping: no reply ({reply.Status}). Many cameras and firewalls ignore ping, so this alone means little.";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Ping needs OS support that may be missing or restricted; it must never break the report.
            return "Ping: could not be sent (" + e.Message + ")";
        }
    }

    private static async Task<(string Line, bool IsVisca, ViscaProbeTarget Target, bool PlainReply)> ProbeUdpAsync(
        IPAddress address, ViscaProbeTarget target, TimeSpan wait, CancellationToken ct)
    {
        var label = $"UDP {target.Port} ({(target.UseHeader ? "VISCA with header" : "raw VISCA")}): ";
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            DisableUdpConnectionReset(udp);
            await udp.SendAsync(ViscaProbe.BuildInquiry(target.UseHeader), new IPEndPoint(address, target.Port), ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(wait);
            while (true)
            {
                UdpReceiveResult reply;
                try { reply = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false); }
                catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused) { return (label + "port closed (the camera refused it)", false, target, false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (label + "no reply", false, target, false); }

                if (!reply.RemoteEndPoint.Address.Equals(address))
                    continue;

                var isVisca = ViscaProbe.IsViscaReply(reply.Buffer, reply.Buffer.Length);
                var from = reply.RemoteEndPoint.Port == target.Port ? "" : $" from port {reply.RemoteEndPoint.Port}";
                var kind = !isVisca ? "not a VISCA answer"
                    : ViscaProbe.IsHeaderedReply(reply.Buffer, reply.Buffer.Length) ? "looks like VISCA (with header)"
                    : "looks like VISCA (plain, without header)";
                return ($"{label}reply{from}: {Hex(reply.Buffer)} - {kind}", isVisca, target, isVisca && !ViscaProbe.IsHeaderedReply(reply.Buffer, reply.Buffer.Length));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            return (label + "error: " + e.Message, false, target, false);
        }
    }

    private static async Task<string> ProbeTcpAsync(IPAddress address, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(1.2));
            await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            return $"TCP {port}: open";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is OperationCanceledException or SocketException or IOException)
        {
            return $"TCP {port}: closed or blocked";
        }
    }

    private static async Task<string> ProbeOnvifAsync(IPAddress address, int port, TimeSpan wait, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            DisableUdpConnectionReset(udp);
            var probe = Encoding.UTF8.GetBytes(WsDiscovery.BuildProbe(Guid.NewGuid()));
            await udp.SendAsync(probe, new IPEndPoint(address, port), ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(wait);
            while (true)
            {
                UdpReceiveResult reply;
                try { reply = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false); }
                catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused) { return "ONVIF discovery (UDP 3702): not available"; }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return "ONVIF discovery (UDP 3702): no reply"; }

                var camera = WsDiscovery.ParseProbeMatch(reply.Buffer, reply.Buffer.Length, reply.RemoteEndPoint.Address);
                if (camera != null)
                    return $"ONVIF discovery (UDP 3702): answers{(camera.Name != null ? $" as \"{camera.Name}\"" : "")}{(camera.Hardware != null ? $" ({camera.Hardware})" : "")}";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            return "ONVIF discovery (UDP 3702): error: " + e.Message;
        }
    }

    private static string Hex(byte[] data)
    {
        var shown = data.Take(24);
        return BitConverter.ToString(shown.ToArray()) + (data.Length > 24 ? "..." : "");
    }

    private static void DisableUdpConnectionReset(UdpClient udp)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try { udp.Client.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null); } // SIO_UDP_CONNRESET
        catch (Exception e) when (e is SocketException or NotSupportedException or InvalidOperationException) { }
    }
}
