// LocalCandidates.cs
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OVS.Rollback.P2P
{
    public static class LocalCandidates
    {
        /// <summary>
        /// This machine's IPv4 addresses on interfaces that are up, excluding loopback, each with the node's port:
        /// what a peer on the same LAN (or the same machine) can reach directly, where the public mapping may not
        /// work (hairpin NAT).
        /// </summary>
        public static IReadOnlyList<IPEndPoint> Discover(int port)
        {
            var result = new List<IPEndPoint>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(addr.Address)) continue;
                        if (addr.Address.GetAddressBytes() is [169, 254, _, _]) continue;    // link-local, never routable
                        result.Add(new IPEndPoint(addr.Address, port));
                        if (result.Count == 16) return result;
                    }
                }
            }
            catch (NetworkInformationException)
            {
                // No interface list (restricted container): the public mapping alone has to do.
            }
            return result;
        }

        /// <summary>"host:port" to an IPv4 endpoint, resolving a name; null when it is empty or does not resolve.</summary>
        public static IPEndPoint? Parse(string? hostPort)
        {
            if (string.IsNullOrWhiteSpace(hostPort)) return null;
            int colon = hostPort.LastIndexOf(':');
            if (colon <= 0 || !ushort.TryParse(hostPort[(colon + 1)..], out var port)) return null;
            string host = hostPort[..colon];
            if (IPAddress.TryParse(host, out var ip)) return new IPEndPoint(ip, port);
            try
            {
                var v4 = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                return v4 is null ? null : new IPEndPoint(v4, port);
            }
            catch (SocketException)
            {
                return null;
            }
        }
    }
}
