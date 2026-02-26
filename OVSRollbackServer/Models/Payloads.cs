// Payloads.cs
using System;

namespace OVS.Rollback.Models
{
    // ──────────────────────────────────────────────
    //  Headers (compact value types)
    // ──────────────────────────────────────────────

    public struct ClientHeader
    {
        public ClientMessageType Type;
        public uint Sequence;
    }

    public struct ServerHeader
    {
        public ServerMessageType Type;
        public uint Sequence;
    }

    // ──────────────────────────────────────────────
    //  Shared Sub-Structures
    // ──────────────────────────────────────────────

    public class ClientPlayerConfigData
    {
        public ushort TeamId { get; set; }
        public ushort PlayerIndex { get; set; }
    }

    public class ClientMatchData
    {
        public string MatchId { get; set; } = "";
        public string Key { get; set; } = "";
        public string EnvironmentId { get; set; } = "";
    }

    public class PlayerStatusData
    {
        public short AveragePing { get; set; }
    }

    // ──────────────────────────────────────────────
    //  Client → Server Payloads
    // ──────────────────────────────────────────────

    public class NewConnectionPayload
    {
        public ushort MessageVersion { get; set; }
        public ClientPlayerConfigData PlayerData { get; set; } = new();
        public ClientMatchData MatchData { get; set; } = new();
    }

    public class InputPayload
    {
        public uint StartFrame { get; set; }
        public uint ClientFrame { get; set; }
        public byte NumFrames { get; set; }
        public byte NumChecksums { get; set; }
        public List<uint> InputPerFrame { get; set; } = [];
        public List<uint> ChecksumPerFrame { get; set; } = [];
    }

    public class PlayerInputAckPayload
    {
        public byte NumPlayers { get; set; }
        public List<uint> AckFrame { get; set; } = [];
        public uint ServerMessageSequenceNumber { get; set; }
    }

    public class MatchResultPayload
    {
        public byte NumPlayers { get; set; }
        public uint LastFrameChecksum { get; set; }
        public byte WinningTeamIndex { get; set; }
    }

    public class QualityDataPayload
    {
        public uint ServerMessageSequenceNumber { get; set; }
    }

    public class DisconnectingPayload
    {
        public byte Reason { get; set; }
    }

    public class PlayerDisconnectedAckPayload
    {
        public byte PlayerDisconnectedArrayIndex { get; set; }
    }

    public class ReadyToStartMatchPayload
    {
        public byte Ready { get; set; }
    }

    // ──────────────────────────────────────────────
    //  Server → Client Payloads
    // ──────────────────────────────────────────────

    public class NewConnectionReplyPayload
    {
        public byte Success { get; set; }
        public byte MatchNumPlayers { get; set; }
        public byte PlayerIndex { get; set; }
        public uint MatchDurationInFrames { get; set; }
        public byte IsValidationServerDebugMode { get; set; }
    }

    public class InputAckPayload
    {
        public uint AckFrame { get; set; }
    }

    public class PlayerInputPayload
    {
        public byte NumPlayers { get; set; }
        public List<uint> StartFrame { get; set; } = [];
        public List<byte> NumFrames { get; set; } = [];
        public ushort NumPredictedOverrides { get; set; }
        public ushort NumZeroedOverrides { get; set; }
        public short Ping { get; set; }
        public short PacketsLossPercent { get; set; }
        public float Rift { get; set; }
        public uint ChecksumAckFrame { get; set; }
        public List<List<uint>> InputPerFrame { get; set; } = [];
    }

    public class RequestQualityDataPayload
    {
        public short Ping { get; set; }
        public short PacketsLossPercent { get; set; }
    }

    public class PlayersStatusPayload
    {
        public byte NumPlayers { get; set; }
        public List<PlayerStatusData> Status { get; set; } = [];
    }

    public class KickPayload
    {
        public ushort Reason { get; set; }
        public uint Param1 { get; set; }
    }

    public class ChecksumAckPayload
    {
        public uint AckFrame { get; set; }
    }

    public class PlayersConfigurationDataPayload
    {
        public byte NumPlayers { get; set; }
        public List<ushort> ConfigValues { get; set; } = [];
    }

    public class PlayerDisconnectedPayload
    {
        public byte PlayerIndex { get; set; }
        public byte ShouldAiTakeControl { get; set; }
        public uint AiTakeControlFrame { get; set; }
        public ushort PlayerDisconnectedArrayIndex { get; set; }
    }

    public class ChangePortPayload
    {
        public ushort Port { get; set; }
    }
}
