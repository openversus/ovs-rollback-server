// OvsModels.cs
using System;
using System.Text.Json.Serialization;

namespace OVS.Rollback.Models
{
    public class OvsPlayer
    {
        [JsonPropertyName("player_index")]
        public ushort PlayerIndex { get; set; }

        [JsonPropertyName("ip")]
        public string Ip { get; set; } = "";

        [JsonPropertyName("is_host")]
        public bool IsHost { get; set; }
    }

    public class OVSMatchConfig
    {
        [JsonPropertyName("max_players")]
        public int MaxPlayers { get; set; } = 6;

        [JsonPropertyName("match_duration")]
        public uint MatchDuration { get; set; } = 36000;

        [JsonPropertyName("players")]
        public List<OvsPlayer> Players { get; set; } = [];
    }
}
