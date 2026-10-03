// InputRecordingPayload.cs
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OVS.Rollback.Models
{
    /// <summary>
    /// A match's recorded inputs, posted to the OVS server's /ovs_match_inputs when the match
    /// ends (or the server shuts down with the match still running). One entry per recorded
    /// player slot; the OVS server stores one document per player per match.
    /// </summary>
    public class InputRecordingPayload
    {
        [JsonPropertyName("matchId")]
        public string MatchId { get; set; } = "";

        [JsonPropertyName("key")]
        public string Key { get; set; } = "";

        /// <summary>"AllPlayersDisconnected" (the match ended normally) or "Shutdown" (MementoMori, SIGINT, a terminating error: a partial recording).</summary>
        [JsonPropertyName("endedBy")]
        public string EndedBy { get; set; } = "";

        [JsonPropertyName("startedAtUtc")]
        public string StartedAtUtc { get; set; } = "";

        [JsonPropertyName("endedAtUtc")]
        public string EndedAtUtc { get; set; } = "";

        [JsonPropertyName("frameRate")]
        public int FrameRate { get; set; } = 60;

        /// <summary>The match's configured duration (match_duration from /ovs_register).</summary>
        [JsonPropertyName("durationFrames")]
        public uint DurationFrames { get; set; }

        /// <summary>Inputs not recorded: from a PlayerIndex outside the match's slots (already dropped by the server), or for a frame past the recording's end.</summary>
        [JsonPropertyName("droppedInputs")]
        public long DroppedInputs { get; set; }

        [JsonPropertyName("players")]
        public List<InputRecordingPlayer> Players { get; set; } = [];
    }

    public class InputRecordingPlayer
    {
        [JsonPropertyName("playerIndex")]
        public int PlayerIndex { get; set; }

        [JsonPropertyName("playerId")]
        public string PlayerId { get; set; } = "";

        [JsonPropertyName("playerName")]
        public string PlayerName { get; set; } = "";

        [JsonPropertyName("playerCharacter")]
        public string PlayerCharacter { get; set; } = "";

        /// <summary>Frames covered: 0 .. Frames - 1 (one past the highest frame received).</summary>
        [JsonPropertyName("frames")]
        public uint Frames { get; set; }

        /// <summary>Frames in that range for which an input was received.</summary>
        [JsonPropertyName("receivedFrames")]
        public uint ReceivedFrames { get; set; }

        /// <summary>How <see cref="Inputs"/> is encoded: "u32le-gzip", one little-endian uint32 per frame, gzipped.</summary>
        [JsonPropertyName("encoding")]
        public string Encoding { get; set; } = "u32le-gzip";

        /// <summary>The inputs, base64 (in JSON). A frame never received is 0 here and listed in <see cref="Missing"/>.</summary>
        [JsonPropertyName("inputs")]
        public byte[] Inputs { get; set; } = [];

        /// <summary>Frame ranges (inclusive [from, to]) for which no input was received.</summary>
        [JsonPropertyName("missing")]
        public List<uint[]> Missing { get; set; } = [];
    }
}
