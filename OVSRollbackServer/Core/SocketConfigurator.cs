// SocketConfigurator.cs
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using OVS.Rollback.Configuration;

namespace OVS.Rollback.Core
{
    /// <summary>
    /// Configures UDP socket for low-latency game traffic.
    /// Sets once at bind time — never modifies game state.
    /// </summary>
    public static class SocketConfigurator
    {
        private const int DscpExpeditedForwarding = 46 << 2; // 0xB8

        /// <param name="networking">Its SendBufferSize and ReceiveBufferSize are the buffers asked for (bytes; 0 or less
        /// leaves the system's default). On a P2P node they are fairness settings: the server's update or the built-in value.</param>
        public static void ConfigureForLowLatency(Socket socket, ILogger logger, NetworkingSettings networking)
        {
            // ── 1. DSCP / Expedited Forwarding ──
            try
            {
                socket.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.TypeOfService,
                    DscpExpeditedForwarding);

                logger.LogInformation(
                    "DSCP Expedited Forwarding (EF/46, ToS=0x{Tos:X2}) enabled on socket",
                    DscpExpeditedForwarding);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    logger.LogWarning(
                        "Windows may override DSCP markings. " +
                        "Ensure a Group Policy QoS rule is configured for DSCP 46.");
                }
            }
            catch (SocketException ex)
            {
                logger.LogWarning(ex,
                    "Failed to set DSCP/EF on socket (non-fatal)");
            }

            // ── 2. Send/receive buffers (Networking.SendBufferSize / ReceiveBufferSize) ──
            SetBuffer(socket, logger, SocketOptionName.SendBuffer, networking.SendBufferSize);
            SetBuffer(socket, logger, SocketOptionName.ReceiveBuffer, networking.ReceiveBufferSize);

            // ── DontFragment REMOVED ──
            //
            // DontFragment = true causes SILENT PACKET DROPS for players
            // behind VPNs, tunnels, or ISPs with reduced MTU (< 576).
            // UDP SendTo does not report ICMP "Fragmentation Needed" errors
            // back to the application, so packets are lost without any
            // indication. Our packets are small (< 500 bytes) so
            // fragmentation is rare anyway, and when it does happen,
            // reassembly at the receiver is preferable to silent loss.
        }

        /// <summary>
        /// Asks for <paramref name="requested"/> bytes and logs what the system granted, read back: a log line naming the
        /// size asked for would claim a buffer the system may not have given.
        /// </summary>
        private static void SetBuffer(Socket socket, ILogger logger, SocketOptionName option, int requested)
        {
            bool receive = option == SocketOptionName.ReceiveBuffer;
            string name = receive ? "receive" : "send";
            if (requested <= 0)
            {
                logger.LogInformation("Socket {Name} buffer left at the system's default", name);
                return;
            }
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, option, requested);
                int reported = (int)socket.GetSocketOption(SocketOptionLevel.Socket, option)!;
                // Linux keeps twice what it grants (the other half is its own bookkeeping) and reports that, and caps the
                // grant at net.core.rmem_max / wmem_max (212992 bytes on a stock kernel) for anyone without CAP_NET_ADMIN.
                int granted = OperatingSystem.IsLinux() ? reported / 2 : reported;
                if (granted < requested)
                {
                    logger.LogWarning("Socket {Name} buffer: {Granted} KB of the {Requested} KB asked for{Why}", name, granted / 1024, requested / 1024,
                        OperatingSystem.IsLinux() ? $"; the system caps it at net.core.{(receive ? "rmem_max" : "wmem_max")}, which a host can raise (sysctl)" : "");
                }
                else
                {
                    logger.LogInformation("Socket {Name} buffer: {Granted} KB", name, granted / 1024);
                }
            }
            catch (SocketException ex)
            {
                logger.LogWarning(ex, "Failed to set the socket {Name} buffer (non-fatal)", name);
            }
        }
    }
}
