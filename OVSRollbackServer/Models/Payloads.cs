// Payloads.cs
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
            JsonSerializerOptions options = new() {
                WriteIndented = true,
                IncludeFields = true,
                IndentSize = 4,
                MaxDepth = 10,
                NewLine = "\n",
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            };

            return Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(this, options));
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
        public short PacketLossPercent { get; set; }
        public float Rift { get; set; }
        public uint ChecksumAckFrame { get; set; }
        public List<List<uint>> InputPerFrame { get; set; } = [];
    }

    public class RequestQualityDataPayload
    {
        public short Ping { get; set; }
        public short PacketLossPercent { get; set; }
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
