// Payloads.cs
using OVS.Rollback.Common;
using OVS.Rollback.Interfaces;
using OVS.Rollback.Models;
using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        public string MatchId { get; set; } = String.Empty;
        public string Key { get; set; } = String.Empty;
        public string EnvironmentId { get; set; } = String.Empty;
    }

    public class PlayerStatusData
    {
        public short AveragePing { get; set; }
    }

    [Serializable]
    public class MatchStatus : IMatchStatus
    {
        private List<string> _playerIdsList = new List<string>();
        public ITimeObject Timestamp { get; private set; }
        public string Event { get; set; } = String.Empty;
        public string Description { get; set; } = String.Empty;

        [JsonPropertyName("matchId")]
        public string MatchId { get; set; } = String.Empty;

        [JsonPropertyName("key")]
        public string Key { get; set; } = String.Empty;
        public int NumPlayers => _playerIdsList.Count;
        public string PlayerId { get; set; } = String.Empty;
        public string[] PlayerIds
        {
            get {
                if (_playerIdsList.Count == 1)
                {
                    PlayerId = _playerIdsList[0];
                }
                return _playerIdsList.ToArray();
            }
        }

        public MatchStatus()
        {
            Timestamp = new TimeObject();
        }

        public MatchStatus(string triggeredEvent, string description, string matchId, string key)
        {
            Timestamp = new TimeObject();
            Event = triggeredEvent;
            Description = description;
            MatchId = matchId;
            Key = key;
        }

        public MatchStatus(string triggeredEvent, string description, string matchId, string key, string playerId)
        {
            Timestamp = new TimeObject();
            Event = triggeredEvent;
            Description = description;
            MatchId = matchId;
            Key = key;
            _playerIdsList.Add(playerId);
        }

        public MatchStatus(string triggeredEvent, string description, string matchId, string key, IEnumerable<string> playerIds)
        {
            Timestamp = new TimeObject();
            Event = triggeredEvent;
            Description = description;
            MatchId = matchId;
            Key = key;
            foreach (string playerId in playerIds)
            {
                _playerIdsList.Add(playerId);
            }
        }

        public MatchStatus(string triggeredEvent, string description, string matchId, string key, string playerId, IEnumerable<string> playerIds)
        {
            Timestamp = new TimeObject();
            Event = triggeredEvent;
            Description = description;
            MatchId = matchId;
            Key = key;
            PlayerId = playerId;
            foreach (string playerId2 in playerIds)
            {
                _playerIdsList.Add(playerId2);
            }
        }

        public void AddPlayer(string playerId)
        {
            _playerIdsList.Add(playerId);
        }

        public string ToJson()
        {
            return Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(this, SnakeCaseJsonContext.Default.MatchStatus));
        }
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

    /// <summary>
    /// Client type 2. Built by the client at 0x14125f480 (final build).
    /// </summary>
    public class InputPayload
    {
        /// <summary>
        /// The client's FirstFrameServerNeeds: one past the highest frame of its own inputs the
        /// server has echoed back to it. Echoing a client's own slot is what acknowledges its inputs.
        /// </summary>
        public uint StartFrame { get; set; }
        /// <summary>The client's simulated frame (not StartFrame + input delay).</summary>
        public uint ClientFrame { get; set; }
        /// <summary>
        /// Every frame not yet echoed, from StartFrame, at most 30. When the server falls more
        /// than 30 behind, the client keeps resending the oldest 30.
        /// </summary>
        public byte NumFrames { get; set; }
        /// <summary>
        /// Zero during play. Non-zero only after the match has ended locally (session states
        /// 7-9), when a frozen StartFrame lets the checksum window open.
        /// </summary>
        public byte NumChecksums { get; set; }
        public List<uint> InputPerFrame { get; set; } = [];
        /// <summary>
        /// Checksum i is for frame StartFrame + i. 0 means the client no longer holds that frame
        /// (or it is in its future). 0x0000CE58 is a placeholder for frames off the checksum
        /// interval (2 in practice). Neither is a real checksum.
        /// </summary>
        public List<uint> ChecksumPerFrame { get; set; } = [];
    }

    public class PlayerInputAckPayload
    {
        public byte NumPlayers { get; set; }
        public List<uint> AckFrame { get; set; } = [];
        public uint ServerMessageSequenceNumber { get; set; }
    }

    /// <summary>
    /// Client type 4. Sent every tick once the match has ended locally (session state 7), until the
    /// client leaves the match (it sends Disconnecting then) or the server replies with
    /// <see cref="ServerMessageType.EndOfMatchAck"/>. LastFrameChecksum is the checksum of whatever
    /// frame the client is on at that tick, which the message does not identify, so it cannot be
    /// compared between clients.
    /// </summary>
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

    /// <summary>
    /// Client type 9, the automatic reply to PlayerDisconnected (server type 11): the low byte of
    /// that message's <see cref="PlayerDisconnectedPayload.PlayerDisconnectedArrayIndex"/>.
    /// </summary>
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
        /// <summary>Sizes the client's per-player input arrays and its checksum cache.</summary>
        public uint MatchDurationInFrames { get; set; }
        /// <summary>
        /// Non-zero turns on the client's "a player is missing" flag, driven by -1 entries in
        /// <see cref="PlayersStatusPayload"/>. What the game does with that flag is not known.
        /// </summary>
        public byte TrackMissingPlayers { get; set; }
        /// <summary>
        /// Only matters to a client started with -ValidationServer (a headless client the original
        /// backend attached to matches): there, 0 sets its checksum interval to 0xFFFF, which turns
        /// checksums off. Ordinary players ignore it.
        /// </summary>
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
        /// <summary>The client keeps the highest value it has ever been sent, as a statistic.</summary>
        public ushort NumPredictedOverrides { get; set; }
        /// <summary>The client keeps the highest value it has ever been sent, as a statistic.</summary>
        public ushort NumZeroedOverrides { get; set; }
        /// <summary>
        /// Milliseconds. The client raises its own input delay from this value (one frame per
        /// ~24 ms above 60 ms, up to 10) and never lowers it during a match, so every value sent
        /// is acted on.
        /// </summary>
        public short Ping { get; set; }
        /// <summary>Hundredths of a percent on the wire: the client multiplies by 0.01.</summary>
        public short PacketLossPercent { get; set; }
        /// <summary>
        /// Frames; sent as int16 × 100. Positive = client ahead (it slows down), negative = behind.
        /// |Rift| ≤ 1 does nothing on the client, and above 50 it disconnects.
        /// </summary>
        public float Rift { get; set; }
        /// <summary>
        /// The client stores this + 1 as NextFrameServerNeedsChecksum, a statistic only. It does
        /// not change what the client sends or lets it free any history.
        /// </summary>
        public uint ChecksumAckFrame { get; set; }
        public List<List<uint>> InputPerFrame { get; set; } = [];
    }

    public class RequestQualityDataPayload
    {
        public short Ping { get; set; }
        /// <summary>Whole percent: unlike PlayerInput, the client does not scale this one.</summary>
        public short PacketLossPercent { get; set; }
    }

    /// <summary>
    /// One int16 per slot: that player's ping, or -1 for a missing player. The client takes its
    /// own entry as its ping.
    /// </summary>
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
        /// <summary>
        /// Counted in checksum intervals, not frames: the client sets NextFrameServerNeedsChecksum
        /// to (AckFrame + 1) × interval. Like ChecksumAckFrame, only a statistic.
        /// </summary>
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
        public byte ShouldAITakeControl { get; set; }
        public uint AITakeControlFrame { get; set; }
        public ushort PlayerDisconnectedArrayIndex { get; set; }
    }

    public class ChangePortPayload
    {
        public ushort Port { get; set; }
    }


    // ──────────────────────────────────────────────
    //  Server → Server Payloads
    // ──────────────────────────────────────────────

    [Serializable]
    public class RegisterPayload
    {
        [JsonPropertyName("matchId")]
        public string MatchId { get; set; } = String.Empty;

        [JsonPropertyName("key")]
        public string Key { get; set; } = String.Empty;

        [JsonPropertyName("hostname")]

        public static readonly string Hostname = Utilities.Hostname;
    }

    // Yes, this is the same as RegisterPayload now, but it's conceptually a different action and may diverge in the future, so it deserves its own type for clarity and maintainability
    [Serializable]
    public class EndMatchPayload
    {
        [JsonPropertyName("matchId")]
        public string MatchId { get; set; } = String.Empty;

        [JsonPropertyName("key")]
        public string Key { get; set; } = String.Empty;

        [JsonPropertyName("hostname")]

        public static readonly string Hostname = Utilities.Hostname;
    }

    [Serializable]
    public class MatchStatusResponse
    { }
}
