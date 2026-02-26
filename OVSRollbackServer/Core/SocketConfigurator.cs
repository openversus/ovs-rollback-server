// SocketConfigurator.cs
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace OVS.Rollback.Core
{
    /// <summary>
    /// Configures UDP socket for low-latency game traffic.
    /// Sets once at bind time — never modifies game state.
    /// </summary>
    public static class SocketConfigurator
    {
        private const int DscpExpeditedForwarding = 46 << 2; // 0xB8
        private const int SocketBufferSize = 512 * 1024;

        public static void ConfigureForLowLatency(Socket socket, ILogger logger)
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

            // ── 2. Enlarged send/receive buffers ──
            try
            {
                socket.SetSocketOption(
                    SocketOptionLevel.Socket,
                    SocketOptionName.SendBuffer,
                    SocketBufferSize);
                socket.SetSocketOption(
                    SocketOptionLevel.Socket,
                    SocketOptionName.ReceiveBuffer,
                    SocketBufferSize);

                logger.LogInformation(
                    "Socket buffers set to {Size} KB (send + receive)",
                    SocketBufferSize / 1024);
            }
            catch (SocketException ex)
            {
                logger.LogWarning(ex, "Failed to set socket buffer sizes (non-fatal)");
            }

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
    }
}
