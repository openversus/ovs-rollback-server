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
}
