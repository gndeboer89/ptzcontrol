using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using PtzJoystickControl.Core.Discovery;

namespace PtzJoystickControl.Application.Discovery;

/// <summary>ONVIF WS-Discovery message building and parsing. No networking here, so it is unit-testable.</summary>
public static class WsDiscovery
{
    public static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.255.250");
    public const int Port = 3702;

    public static string BuildProbe(Guid messageId) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<e:Envelope xmlns:e=\"http://www.w3.org/2003/05/soap-envelope\" " +
        "xmlns:w=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\" " +
        "xmlns:d=\"http://schemas.xmlsoap.org/ws/2005/04/discovery\" " +
        "xmlns:dn=\"http://www.onvif.org/ver10/network/wsdl\">" +
        "<e:Header>" +
        $"<w:MessageID>uuid:{messageId}</w:MessageID>" +
        "<w:To e:mustUnderstand=\"true\">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>" +
        "<w:Action e:mustUnderstand=\"true\">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>" +
        "</e:Header>" +
        "<e:Body><d:Probe><d:Types>dn:NetworkVideoTransmitter</d:Types></d:Probe></e:Body>" +
        "</e:Envelope>";

    /// <summary>
    /// Parses a ProbeMatches response. Returns null when the message is not a usable match
    /// (not XML, not a probe match, or no IPv4 address could be determined).
    /// </summary>
    public static DiscoveredCamera? ParseProbeMatch(byte[] data, int length, IPAddress? sender = null)
    {
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(Encoding.UTF8.GetString(data, 0, length)), settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        var match = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ProbeMatch");
        if (match == null)
            return null;

        string? Child(string name) => match.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

        Uri? serviceUri = null;
        foreach (var token in (Child("XAddrs") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Uri.TryCreate(token, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                IPAddress.TryParse(uri.Host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                serviceUri = uri;
                break;
            }
        }

        IPAddress? address = serviceUri != null ? IPAddress.Parse(serviceUri.Host) : sender;
        if (address == null)
            return null;

        string? name = null, hardware = null;
        foreach (var scope in (Child("Scopes") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            const string prefix = "onvif://www.onvif.org/";
            if (!scope.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var rest = scope[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0)
                continue;
            var key = rest[..slash].ToLowerInvariant();
            var value = Uri.UnescapeDataString(rest[(slash + 1)..]);
            if (key == "name") name ??= value;
            else if (key == "hardware") hardware ??= value;
        }

        return new DiscoveredCamera(address)
        {
            Name = name ?? hardware,
            Hardware = hardware,
            OnvifServiceUri = serviceUri,
            Source = DiscoverySource.Onvif,
        };
    }
}
