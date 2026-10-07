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
                    // Recognize spectators by the explicit flag OR the
                    // PlayerIndex >= 8888 sentinel convention — matchmakers
                    // have used either. Missing a spectator here inflates
                    // TeamSlotCount and corrupts the wire-protocol slot count.
                    if (IsSpectatorEntry(player))
                    {
                        count++;
                    }
                }
                return count;
            }
        }
        public int ActualPlayers => Players.Count - NumSpectators;

        /// <summary>The first spectator index: every game client connects as a spectator with it, whatever its slot.</summary>
        public const ushort FirstSpectatorIndex = 8888;

        /// <summary>A spectator's entry: the explicit flag or the PlayerIndex >= 8888 sentinel (see <see cref="NumSpectators"/>).</summary>
        public static bool IsSpectatorEntry(OvsPlayer player) => player.IsSpectator || player.PlayerIndex >= FirstSpectatorIndex;

        /// <summary>
        /// The index of every human of the match among the P2P nodes: a player's own, and for the n spectator entries
        /// 8888 .. 8888+n-1, the series the rendezvous gives out in registration order. The same whether the config
        /// numbers its spectators 8888 + i or all 8888, since no game client sends anything but 8888 anyway.
        /// </summary>
        public HashSet<ushort> NodeIndexes()
        {
            var indexes = new HashSet<ushort>();
            int spectators = 0;
            foreach (var player in Players)
            {
                if (player.IsBot) continue;
                if (IsSpectatorEntry(player)) spectators++;
                else indexes.Add(player.PlayerIndex);
            }
            for (int i = 0; i < spectators; i++)
            {
                indexes.Add((ushort)(FirstSpectatorIndex + i));
            }
            return indexes;
        }

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
