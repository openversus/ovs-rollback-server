// RendezvousService.cs
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace OVS.Rollback.P2P
{
    /// <summary>
    /// The rendezvous over UDP: one socket, one registration in, one <see cref="PeersMessage"/> out, to the address
    /// the registration came from (which is what tells the node its public mapping). Anything that is not a
    /// registration is dropped.
    /// </summary>
    public sealed class RendezvousService(int port, IMatchKeyValidator validator, RendezvousRegistry registry, ILogger logger, IPEndPoint? relay = null)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public async Task RunAsync(CancellationToken ct)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            UdpSockets.IgnoreConnectionReset(socket);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            logger.LogInformation("Rendezvous listening on UDP {Port}; registrations expire after {Ttl}; relay {Relay}",
                port, registry.Ttl, relay?.ToString() ?? "none");

            var buffer = new byte[P2PProtocol.MaxDatagram];
            var any = new IPEndPoint(IPAddress.Any, 0);
            var nextExpiry = _clock.Elapsed + TimeSpan.FromMinutes(1);

            while (!ct.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException e)
                {
                    logger.LogWarning("Receive failed: {Error}", e.SocketErrorCode);
                    continue;
                }

                var from = (IPEndPoint)result.RemoteEndPoint;
                var register = P2PProtocol.DecodeRegister(buffer.AsSpan(0, result.ReceivedBytes));
                if (register is null)
                {
                    logger.LogDebug("Dropped {Bytes} bytes from {From}: not a registration", result.ReceivedBytes, from);
                    continue;
                }

                var now = _clock.Elapsed;
                PeersMessage reply;
                if (!await validator.IsValidAsync(register.MatchId, register.MatchKey, ct))
                {
                    logger.LogWarning("Rejected registration for match {Match} player {Index} from {From}: bad key", register.MatchId, register.PlayerIndex, from);
                    reply = new PeersMessage(false, from, PeersMessage.NoHost, relay, []);
                }
                else
                {
                    reply = registry.Register(register, from, now, relay);
                    ushort index = reply.YourIndex ?? register.PlayerIndex;
                    logger.LogInformation("Match {Match}: player {Index}{Sent}{Host} at {From} (LAN: {Lan}); {Peers} peer(s) known, host {HostIndex}",
                        register.MatchId, index, index != register.PlayerIndex ? $" (registered as {register.PlayerIndex})" : "",
                        register.IsHost ? " (host)" : "", from,
                        string.Join(", ", register.LocalCandidates), reply.Peers.Count,
                        reply.HostIndex == PeersMessage.NoHost ? "unknown" : reply.HostIndex.ToString());
                }

                try
                {
                    await socket.SendToAsync(P2PProtocol.Encode(reply), SocketFlags.None, from, ct);
                }
                catch (SocketException e)
                {
                    logger.LogWarning("Reply to {From} failed: {Error}", from, e.SocketErrorCode);
                }

                if (now >= nextExpiry)
                {
                    nextExpiry = now + TimeSpan.FromMinutes(1);
                    int left = registry.Expire(now);
                    logger.LogDebug("Expiry pass: {Matches} match(es) still registered", left);
                }
            }
        }
    }

    public static class UdpSockets
    {
        /// <summary>
        /// On Windows a UDP socket reports a peer's ICMP "port unreachable" as a ConnectionReset on the next
        /// receive, which would kill a receive loop; SIO_UDP_CONNRESET off makes it drop them like every other OS.
        /// </summary>
        public static void IgnoreConnectionReset(Socket socket)
        {
            if (!OperatingSystem.IsWindows()) return;
            const int SIO_UDP_CONNRESET = -1744830452;
            try { socket.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null); } catch (SocketException) { }
        }
    }
}
