// OvsModels.cs
using System;
using System.Text.Json.Serialization;

namespace OVS.Rollback.Models
{
    public class OvsPlayer
    {
        [JsonPropertyName("player_index")]
        public ushort PlayerIndex { get; set; }

        [JsonPropertyName("player_id")]
        public string PlayerId { get; set; } = String.Empty;

        [JsonPropertyName("player_name")]
        public string PlayerName { get; set; } = String.Empty;

        [JsonPropertyName("player_character")]
        public string PlayerCharacter { get; set; } = String.Empty;

        [JsonPropertyName("ip")]
        public string Ip { get; set; } = String.Empty;

        [JsonPropertyName("is_host")]
        public bool IsHost { get; set; }

        [JsonPropertyName("is_spectator")]
        public bool IsSpectator { get; set; } = false;

        [JsonPropertyName("is_bot")]
        public bool IsBot { get; set; } = false;
    }

    public class OVSMatchConfig
    {
        [JsonPropertyName("max_players")]
        public int MaxPlayers { get; set; } = 6;

        [JsonPropertyName("match_duration")]
        public uint MatchDuration { get; set; } = 36000;

        [JsonPropertyName("players")]
        public List<OvsPlayer> Players { get; set; } = [];

        public int NumSpectators
        {
            get {
                if (null == Players || Players.Count == 0)
                {
                    return 0;
                }

                int count = 0;
                foreach (var player in Players)
                {
                    if (player.IsSpectator)
                    {
                        count++;
                    }
                }
                return count;
            }
        }
        public int ActualPlayers => Players.Count - NumSpectators;

        public int NumBots
        {
            get {
                if (null == Players || Players.Count == 0) return 0;
                int count = 0;
                foreach (var player in Players)
                {
                    if (player.IsBot) count++;
                }
                return count;
            }
        }
    }
}
