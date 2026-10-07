using System.Net;
using OVS.Rollback.Core;
using OVS.Rollback.Models;
using OVS.Rollback.P2P;
using Xunit;

namespace OVS.Rollback.P2P.Tests;

/// <summary>
/// Every game client spectates as 8888, whatever slot it was given. The host's node expects the spectators by ordinal
/// (8888, 8889, ...), the rendezvous hands out that series, and the engine gives each spectator its own config entry in
/// connect order. A config may number its spectators 8888 + i or all 8888; nothing may depend on which.
/// </summary>
public class SpectatorIndexTests
{
    private static OvsPlayer Player(ushort index, string id, bool host = false) =>
        new() { PlayerIndex = index, PlayerId = id, PlayerName = id, PlayerCharacter = "c", IsHost = host };

    private static OvsPlayer Watcher(ushort index, string id, bool flagged = true) =>
        new() { PlayerIndex = index, PlayerId = id, PlayerName = id, PlayerCharacter = "c", IsSpectator = flagged };

    /// <summary>A 1v1 with three spectators: numbered 8888 + i, or all 8888 (one of them recognized by its index alone).</summary>
    private static OVSMatchConfig Config(bool all8888) => new()
    {
        MaxPlayers = 5,
        Players =
        [
            Player(0, "p0", host: true),
            Player(1, "p1"),
            Watcher(8888, "s0"),
            Watcher(all8888 ? (ushort)8888 : (ushort)8889, "s1", flagged: false),
            Watcher(all8888 ? (ushort)8888 : (ushort)8890, "s2"),
            new OvsPlayer { PlayerIndex = 3, PlayerId = "bot", IsBot = true },
        ]
    };

    private static MatchState NewMatch(OVSMatchConfig config) =>
        new() { SpectatorEntries = config.Players.Where(OVSMatchConfig.IsSpectatorEntry).ToArray() };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_host_expects_its_players_and_the_spectators_by_ordinal(bool all8888)
    {
        var config = Config(all8888);
        Assert.Equal(new ushort[] { 0, 1, 8888, 8889, 8890 }, config.NodeIndexes().Order());
        Assert.Equal(3, config.NumSpectators);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_rendezvous_gives_out_exactly_the_series_the_host_expects(bool all8888)
    {
        var config = Config(all8888);
        var r = new RendezvousRegistry(TimeSpan.FromMinutes(10));
        var given = new List<ushort>
        {
            r.Register(new RegisterMessage("m", "k", 0, true, []), new IPEndPoint(IPAddress.Parse("203.0.113.1"), 1), TimeSpan.Zero).YourIndex!.Value,
            r.Register(new RegisterMessage("m", "k", 1, false, []), new IPEndPoint(IPAddress.Parse("203.0.113.2"), 1), TimeSpan.Zero).YourIndex!.Value,
        };
        for (int i = 0; i < config.NumSpectators; i++)
        {
            // Every game sends 8888, from its own address.
            var from = new IPEndPoint(IPAddress.Parse($"198.51.100.{i + 1}"), 41234);
            given.Add(r.Register(new RegisterMessage("m", "k", 8888, false, []), from, TimeSpan.Zero).YourIndex!.Value);
        }
        Assert.Equal(config.NodeIndexes().Order(), given.Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Spectators_all_sending_8888_each_claim_their_own_entry_in_connect_order(bool all8888)
    {
        var config = Config(all8888);
        var match = NewMatch(config);

        Assert.Equal("s0", RollbackServer.ConfigEntryFor(match, config, 8888, "10.0.0.1:5000")?.PlayerId);
        Assert.Equal("s1", RollbackServer.ConfigEntryFor(match, config, 8888, "10.0.0.2:5000")?.PlayerId);
        // A connect again from the first address keeps its entry; the next new address gets the next one.
        Assert.Equal("s0", RollbackServer.ConfigEntryFor(match, config, 8888, "10.0.0.1:5000")?.PlayerId);
        Assert.Equal("s2", RollbackServer.ConfigEntryFor(match, config, 8888, "10.0.0.3:5000")?.PlayerId);
        Assert.Equal("s1", RollbackServer.ConfigEntryFor(match, config, 8889, "10.0.0.2:5000")?.PlayerId);
        // More spectators than entries: nothing left (the mismatch warning's case).
        Assert.Null(RollbackServer.ConfigEntryFor(match, config, 8888, "10.0.0.4:5000"));
    }

    [Fact]
    public void Players_are_still_found_by_their_index()
    {
        var config = Config(all8888: true);
        var match = NewMatch(config);
        Assert.Equal("p1", RollbackServer.ConfigEntryFor(match, config, 1, "10.0.0.9:5000")?.PlayerId);
        Assert.Equal("p0", RollbackServer.ConfigEntryFor(match, config, 0, "10.0.0.8:5000")?.PlayerId);
        Assert.Null(RollbackServer.ConfigEntryFor(match, config, 2, "10.0.0.7:5000"));
        Assert.Null(RollbackServer.ConfigEntryFor(match, null, 1, "10.0.0.9:5000"));
        // A player's lookup claims no spectator entry.
        Assert.Equal("s0", RollbackServer.ConfigEntryFor(match, config, 8888, "10.0.0.9:5000")?.PlayerId);
    }
}
