// Constants.cs
using System;
namespace OVS.Rollback.Core
{
    public static class Constants
    {
        public const ushort GameServerPort = 41234;
        public const int MaxPlayers = 8;
        public const bool EmulateP2 = false;

        /// <summary>
        /// Limits of the game client's PlayerInput parser (0x141216940 in the final build). It
        /// fills a fixed 0x210-byte struct: 4 slots of start frame and frame count, and 30 input
        /// words per slot. It checks neither count against those limits, so exceeding either
        /// makes the client write past its own storage: a 31st frame overwrites the next slot's
        /// first input, and a 5th slot runs past the end of the struct.
        /// </summary>
        public static class ClientLimits
        {
            public const int MaxFramesPerSlot = 30;
            public const int MaxSlots = 4;
        }

        /// <summary>
        /// Values the client's checksum function (0x141241b30) returns that are not checksums.
        /// </summary>
        public static class ClientChecksums
        {
            /// <summary>The client no longer holds that frame (past its rollback buffer) or has not reached it.</summary>
            public const uint NotHeld = 0;
            /// <summary>Placeholder for frames off the client's checksum interval (every 2nd frame in practice).</summary>
            public const uint OffInterval = 0x0000CE58;
        }

        /// <summary>
        /// The response header carrying the server's signature over the exact body bytes (ECDSA P-256, SHA-256, IEEE
        /// P1363 r||s, base64), on /ovs_register and /ovs_node_config; the P2P node checks it, a relay ignores it.
        /// </summary>
        public const string SignatureHeader = "X-OVS-Signature";

        public static class Endpoints
        {
            internal const string OVSRegister = "/ovs_register";
            internal const string OVSEndMatch = "/ovs_end_match";
            internal const string OVSMatchStatus = "/api/ovs_match_status";
            /// <summary>Match id + key: the match has started (the TS route exists for exactly this; a P2P host node posts it).</summary>
            internal const string OVSMatchStarted = "/ovs_match_started";
            /// <summary>Match id + key: a P2P host node is serving, so the players may be told their server is ready.</summary>
            internal const string OVSP2PReady = "/ovs_p2p_ready";
            /// <summary>Match id + key: no peer path opened; the server deploys the relay (once) and answers {host, port}.</summary>
            internal const string OVSP2PFailed = "/ovs_p2p_failed";
            /// <summary>GET: the signed settings update for P2P nodes, {"version": N, "config": {sections}}.</summary>
            internal const string OVSNodeConfig = "/ovs_node_config";
            internal const string MVSIRegister = "/mvsi_register";
            internal const string MVSIEndMatch = "/mvsi_end_match";
        }
    }
}
