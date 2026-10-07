using System.Net;
using OVS.Rollback.P2P;
using Xunit;

namespace OVS.Rollback.P2P.Tests;

public class RendezvousRegistryTests
{
    private static IPEndPoint Ep(string ip, int port) => new(IPAddress.Parse(ip), port);
    private static readonly IPEndPoint A = Ep("203.0.113.1", 41234), B = Ep("203.0.113.2", 50000);

    [Fact]
    public void Two_nodes_of_one_match_learn_each_other_and_the_host()
    {
        var r = new RendezvousRegistry(TimeSpan.FromMinutes(10));
        var t = TimeSpan.Zero;

        var first = r.Register(new RegisterMessage("m1", "key", 0, true, [Ep("10.0.0.1", 41234)]), A, t);
        Assert.True(first.Accepted);
        Assert.Equal(A, first.YourPublicEndPoint);
        Assert.Equal((ushort)0, first.HostIndex);
        Assert.Empty(first.Peers);

        var second = r.Register(new RegisterMessage("m1", "key", 1, false, []), B, t + TimeSpan.FromSeconds(1));
        Assert.True(second.Accepted);
        Assert.Equal(B, second.YourPublicEndPoint);
        Assert.Equal((ushort)0, second.HostIndex);
        var peer = Assert.Single(second.Peers);
        Assert.Equal(new PeerInfo(0, true, A, [Ep("10.0.0.1", 41234)]) with { LocalCandidates = peer.LocalCandidates }, peer);
        Assert.Equal([Ep("10.0.0.1", 41234)], peer.LocalCandidates);

        // The host re-registers and now sees the guest.
        var again = r.Register(new RegisterMessage("m1", "key", 0, true, []), A, t + TimeSpan.FromSeconds(2));
        Assert.Equal((ushort)1, Assert.Single(again.Peers).PlayerIndex);
        Assert.Equal(1, r.MatchCount);
    }

    [Fact]
    public void Host_is_unknown_until_the_host_registers()
    {
        var r = new RendezvousRegistry(TimeSpan.FromMinutes(10));
        var reply = r.Register(new RegisterMessage("m1", "key", 1, false, []), B, TimeSpan.Zero);
        Assert.Equal(PeersMessage.NoHost, reply.HostIndex);
    }

    [Fact]
    public void A_different_key_for_a_registered_match_is_rejected()
    {
        var r = new RendezvousRegistry(TimeSpan.FromMinutes(10));
        r.Register(new RegisterMessage("m1", "key", 0, true, []), A, TimeSpan.Zero);
        var reply = r.Register(new RegisterMessage("m1", "KEY", 1, false, []), B, TimeSpan.Zero);
        Assert.False(reply.Accepted);
        Assert.Empty(reply.Peers);
        // and the impostor was not recorded
        var host = r.Register(new RegisterMessage("m1", "key", 0, true, []), A, TimeSpan.Zero);
        Assert.Empty(host.Peers);
    }

    [Fact]
    public void Matches_are_separate_and_the_relay_is_passed_through()
    {
        var r = new RendezvousRegistry(TimeSpan.FromMinutes(10));
        r.Register(new RegisterMessage("m1", "key", 0, true, []), A, TimeSpan.Zero);
        var relay = Ep("198.51.100.9", 57000);
        var other = r.Register(new RegisterMessage("m2", "other", 0, true, []), B, TimeSpan.Zero, relay);
        Assert.Empty(other.Peers);
        Assert.Equal(relay, other.Relay);
        Assert.Equal(2, r.MatchCount);
    }

    [Fact]
    public void Silent_nodes_expire_and_empty_matches_go()
    {
        var r = new RendezvousRegistry(TimeSpan.FromMinutes(10));
        r.Register(new RegisterMessage("m1", "key", 0, true, []), A, TimeSpan.Zero);
        r.Register(new RegisterMessage("m1", "key", 1, false, []), B, TimeSpan.FromMinutes(5));
        Assert.Equal(1, r.Expire(TimeSpan.FromMinutes(10)));          // nobody is older than the TTL yet
        Assert.Equal(1, r.Expire(TimeSpan.FromMinutes(11)));          // node 0 gone, node 1 stays
        var reply = r.Register(new RegisterMessage("m1", "key", 2, false, []), A, TimeSpan.FromMinutes(11));
        Assert.Equal((ushort)1, Assert.Single(reply.Peers).PlayerIndex);
        Assert.Equal(0, r.Expire(TimeSpan.FromMinutes(30)));
    }

    private static readonly IPEndPoint S1 = Ep("198.51.100.1", 41234), S2 = Ep("198.51.100.2", 41234), S3 = Ep("198.51.100.3", 41234);

    private static RegisterMessage Spectator(ushort index = 8888) => new("m1", "key", index, false, []);

    [Fact]
    public void Two_spectators_both_sending_8888_get_8888_and_8889_and_each_sees_the_other()
    {
        var r = new RendezvousRegistry(TimeSpan.FromMinutes(10));
        var t = TimeSpan.Zero;
        r.Register(new RegisterMessage("m1", "key", 0, true, []), A, t);

        var first = r.Register(Spectator(), S1, t);
        Assert.Equal((ushort)8888, first.YourIndex);

        var second = r.Register(Spectator(), S2, t);
        Assert.Equal((ushort)8889, second.YourIndex);
        // Everyone but itself, by the index it was given: the first spectator is in, the second is not.
        Assert.Equal([(0, A), (8888, S1)], second.Peers.Select(p => ((int)p.PlayerIndex, p.PublicEndPoint)).OrderBy(p => p.Item1));

        var host = r.Register(new RegisterMessage("m1", "key", 0, true, []), A, t);
        Assert.Equal([(8888, S1), (8889, S2)], host.Peers.Select(p => ((int)p.PlayerIndex, p.PublicEndPoint)).OrderBy(p => p.Item1));
    }

    [Fact]
    public void A_spectator_re_registering_from_the_same_address_keeps_its_index()
    {
        var r = new RendezvousRegistry(TimeSpan.FromMinutes(10));
        var t = TimeSpan.Zero;
        Assert.Equal((ushort)8888, r.Register(Spectator(), S1, t).YourIndex);
        Assert.Equal((ushort)8889, r.Register(Spectator(), S2, t).YourIndex);

        // Again with the game's 8888, and with the index it was given (what a node sends once it knows it).
        Assert.Equal((ushort)8889, r.Register(Spectator(), S2, t + TimeSpan.FromSeconds(1)).YourIndex);
        Assert.Equal((ushort)8889, r.Register(Spectator(8889), S2, t + TimeSpan.FromSeconds(2)).YourIndex);
        Assert.Equal((ushort)8888, r.Register(Spectator(8889), S1, t + TimeSpan.FromSeconds(3)).YourIndex);
        var host = r.Register(new RegisterMessage("m1", "key", 0, true, []), A, t + TimeSpan.FromSeconds(4));
        Assert.Equal(2, host.Peers.Count);
    }

    [Fact]
    public void An_expired_spectator_frees_its_index_for_the_next()
    {
        var r = new RendezvousRegistry(TimeSpan.FromMinutes(10));
        Assert.Equal((ushort)8888, r.Register(Spectator(), S1, TimeSpan.Zero).YourIndex);
        Assert.Equal((ushort)8889, r.Register(Spectator(), S2, TimeSpan.FromMinutes(5)).YourIndex);
        r.Expire(TimeSpan.FromMinutes(11));                                   // S1 is gone, S2 stays

        Assert.Equal((ushort)8888, r.Register(Spectator(), S3, TimeSpan.FromMinutes(11)).YourIndex);
        Assert.Equal((ushort)8889, r.Register(Spectator(), S2, TimeSpan.FromMinutes(12)).YourIndex);
    }

    [Fact]
    public void Players_keep_their_own_index_and_are_told_it()
    {
        var r = new RendezvousRegistry(TimeSpan.FromMinutes(10));
        Assert.Equal((ushort)8888, r.Register(Spectator(), S1, TimeSpan.Zero).YourIndex);
        Assert.Equal((ushort)2, r.Register(new RegisterMessage("m1", "key", 2, true, []), A, TimeSpan.Zero).YourIndex);
        Assert.Equal((ushort)1, r.Register(new RegisterMessage("m1", "key", 1, false, []), B, TimeSpan.Zero).YourIndex);

        // A player that comes back from another address is still the same index (and replaces its old entry).
        var moved = r.Register(new RegisterMessage("m1", "key", 1, false, []), S2, TimeSpan.Zero);
        Assert.Equal((ushort)1, moved.YourIndex);
        var host = r.Register(new RegisterMessage("m1", "key", 2, true, []), A, TimeSpan.Zero);
        Assert.Equal([(1, S2), (8888, S1)], host.Peers.Select(p => ((int)p.PlayerIndex, p.PublicEndPoint)).OrderBy(p => p.Item1));
    }
}
