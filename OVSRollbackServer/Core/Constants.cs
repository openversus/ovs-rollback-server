// Constants.cs
using System;
namespace OVS.Rollback.Core
{
    public static class Constants
    {
        public const ushort GameServerPort = 41234;
        public const int MaxPlayers = 8;
        public const bool EmulateP2 = false;

        public static class Endpoints
        {
            internal const string OVSRegister = "/ovs_register";
            internal const string OVSEndMatch = "/ovs_end_match";
            internal const string OVSMatchStatus = "/api/ovs_match_status";
            internal const string MVSIRegister = "/mvsi_register";
            internal const string MVSIEndMatch = "/mvsi_end_match";
        }
    }
}
