// AddressMask.cs
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace OVS.Rollback.Utils
{
    /// <summary>
    /// Masks players' IP addresses in log output when the engine runs inside the P2P node on a player's machine, whose
    /// log players share for support: "203.0.113.217:50123" is written "X.X.X.217:50123", an IPv6 address keeps only
    /// its last group. It works on the finished text (<see cref="Serilog.Templates.AddressMaskingTemplate"/> hands it
    /// every line, exception included), so it needs no help from the code that logged: every address is masked unless
    /// it is loopback, unspecified, or an endpoint registered as OVS's own (the rendezvous, a relay, the HTTP server),
    /// and an address nobody thought to classify is hidden rather than shown. Off in the relay, where the addresses are
    /// the operator's to see; the node turns it on, and nothing turns it off.
    /// </summary>
    internal static partial class AddressMask
    {
        private static volatile bool _enabled;
        private static readonly ConcurrentDictionary<IPEndPoint, byte> _infra = new();

        public static bool Enabled => _enabled;

        /// <summary>Masks every line logged from now on.</summary>
        public static void Enable() => _enabled = true;

        /// <summary>An OVS endpoint, shown as itself when the text gives this address with this port.</summary>
        public static void AddInfra(IPEndPoint? endPoint)
        {
            if (endPoint is not null && endPoint.Port != 0)
            {
                _infra[Normalize(endPoint)] = 0;
            }
        }

        /// <summary>
        /// A setting naming an OVS endpoint, "address:port" or a URL, registered when its host is an IP literal; a host
        /// name is printed as the name, which needs nothing. A URL's address counts only with its port, as it is printed.
        /// </summary>
        public static void AddInfra(string? setting)
        {
            if (string.IsNullOrWhiteSpace(setting))
            {
                return;
            }
            setting = setting.Trim();
            if (IPEndPoint.TryParse(setting, out var endPoint))
            {
                AddInfra(endPoint);
            }
            else if (Uri.TryCreate(setting, UriKind.Absolute, out var uri) && uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            {
                AddInfra(new IPEndPoint(IPAddress.Parse(uri.Host.Trim('[', ']')), uri.Port));
            }
        }

        /// <summary><paramref name="text"/> with every address that is not OVS's own masked.</summary>
        public static string Mask(string text)
        {
            text = IPv4().Replace(text, MaskIPv4);
            return text.Contains(':') ? IPv6().Replace(text, MaskIPv6) : text;
        }

        private static string MaskIPv4(Match m)
        {
            string ip = m.Groups["ip"].Value;
            Group port = m.Groups["port"];
            var address = IPAddress.Parse(ip);
            if (Shown(address, port.Success ? int.Parse(port.ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture) : 0))
            {
                return m.Value;
            }
            return "X.X.X." + ip[(ip.LastIndexOf('.') + 1)..] + (port.Success ? ":" + port.Value : "");
        }

        private static string MaskIPv6(Match m)
        {
            // The pattern finds runs of hex groups and colons; only what parses as an IPv6 address is one (a time of day
            // like 18:06:12 has neither eight groups nor "::", and does not).
            Group port = m.Groups["port"];
            if (!IPAddress.TryParse(m.Groups["ip"].Value, out var address) || address.AddressFamily != AddressFamily.InterNetworkV6
                || Shown(address, port.Success ? int.Parse(port.ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture) : 0))
            {
                return m.Value;
            }
            byte[] bytes = address.GetAddressBytes();
            return "X:X:X:X:X:X:X:" + ((bytes[14] << 8) | bytes[15]).ToString("x", CultureInfo.InvariantCulture);
        }

        private static bool Shown(IPAddress address, int port)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }
            return IPAddress.IsLoopback(address)
                || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
                || (port > 0 && port <= 65535 && _infra.ContainsKey(new IPEndPoint(address, port)));
        }

        private static IPEndPoint Normalize(IPEndPoint endPoint) =>
            endPoint.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(endPoint.Address.MapToIPv4(), endPoint.Port) : endPoint;

        // A dotted quad not inside a longer dotted number (a fifth part, a preceding digit), and the port after it if any.
        [GeneratedRegex(@"(?<![\d.])(?<ip>(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d))(?!\.?\d)(?::(?<port>\d{1,5})(?!\d))?", RegexOptions.CultureInvariant)]
        private static partial Regex IPv4();

        // A run of hex groups joined by colons, not part of a longer word, number or address, and the port after it when
        // it is bracketed ("[addr]:port"; the port is only looked at, not matched).
        [GeneratedRegex(@"(?<![\w:.])(?<ip>(?:[0-9A-Fa-f]{1,4}|:)(?::[0-9A-Fa-f]{0,4}){1,7})(?![\w:.])(?:(?=\]:(?<port>\d{1,5})(?!\d)))?", RegexOptions.CultureInvariant)]
        private static partial Regex IPv6();
    }
}
