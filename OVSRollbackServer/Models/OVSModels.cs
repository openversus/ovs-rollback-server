// OvsModels.cs
using System.Text.Json.Serialization;

namespace Rollback.Models;

public class OvsPlayer
{
    [JsonPropertyName("player_index")]
    public ushort PlayerIndex { get; set; }

    [JsonPropertyName("ip")]
    public string Ip { get; set; } = "";

    [JsonPropertyName("is_host")]
    public bool IsHost { get; set; }
}

public class OvsMatchConfig
{
    [JsonPropertyName("max_players")]
    public int MaxPlayers { get; set; } = 2;

    [JsonPropertyName("match_duration")]
    public uint MatchDuration { get; set; } = 36000;

    [JsonPropertyName("players")]
    public List<OvsPlayer> Players { get; set; } = [];
}

