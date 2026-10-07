using System.Net;
using OVS.Rollback.P2P;
using Xunit;

namespace OVS.Rollback.P2P.Tests;

public class PeerPathTests
{
    private static readonly PuncherOptions Options = new(
        ProbeInterval: TimeSpan.FromMilliseconds(100),
        PunchTimeout: TimeSpan.FromSeconds(8),
        KeepAliveInterval: TimeSpan.FromSeconds(1),
        PeerTimeout: TimeSpan.FromSeconds(30));

    private const ulong Hash = 0xfeedbeefUL;
    private static IPEndPoint Ep(string ip, int port) => new(IPAddress.Parse(ip), port);
    private static readonly IPEndPoint Public = Ep("203.0.113.5", 41234);
    private static readonly IPEndPoint Lan = Ep("192.168.1.5", 41234);

    private static PeerPath NewPath(TimeSpan now, params IPEndPoint[] candidates)
    {
        var p = new PeerPath(myIndex: 0, peerIndex: 1, Hash, Options, now);
        p.AddCandidates(candidates);
        return p;
    }

    private static List<(IPEndPoint To, byte[] Datagram)> Sent(PeerPath p, TimeSpan now) => p.Tick(now).ToList();

    [Fact]
    public void Probes_every_candidate_at_the_interval_and_not_between()
    {
        var t = TimeSpan.Zero;
        var p = NewPath(t, Public, Lan);
        var first = Sent(p, t);
        Assert.Equal([Public, Lan], first.Select(s => s.To));
        Assert.All(first, s => Assert.Equal(P2PMessageKind.Probe, P2PProtocol.KindOf(s.Datagram)));
        var probe = P2PProtocol.DecodeProbe(first[0].Datagram)!;
        Assert.Equal((Hash, (ushort)0, (ushort)1), (probe.MatchHash, probe.FromIndex, probe.ToIndex));

        Assert.Empty(Sent(p, t + TimeSpan.FromMilliseconds(99)));
        var second = Sent(p, t + TimeSpan.FromMilliseconds(100));
        Assert.Equal(2, second.Count);
        Assert.NotEqual(probe.Sequence, P2PProtocol.DecodeProbe(second[0].Datagram)!.Sequence);
    }

    [Fact]
    public void Duplicate_and_empty_candidates_are_dropped()
    {
        var p = NewPath(TimeSpan.Zero, Public, Public, Ep("0.0.0.0", 0), Ep("1.2.3.4", 0));
        Assert.Equal([Public], p.Candidates);
    }

    [Fact]
    public void An_ack_opens_the_path_at_the_address_it_came_from()
    {
        var t = TimeSpan.Zero;
        var p = NewPath(t, Public, Lan);
        Sent(p, t);
        var rewritten = Ep("203.0.113.5", 60123);     // the peer's NAT answered from another port
        Assert.True(p.OnAck(new ProbeMessage(Hash, 1, 0, 1), rewritten, t + TimeSpan.FromMilliseconds(40)));
        Assert.Equal(PeerPathState.Open, p.State);
        Assert.Equal(rewritten, p.Live);
        Assert.Empty(Sent(p, t + TimeSpan.FromMilliseconds(100)));      // no more probes
    }

    [Fact]
    public void An_ack_for_another_match_or_peer_is_ignored()
    {
        var p = NewPath(TimeSpan.Zero, Public);
        Assert.False(p.OnAck(new ProbeMessage(Hash + 1, 1, 0, 1), Public, TimeSpan.Zero));
        Assert.False(p.OnAck(new ProbeMessage(Hash, 2, 0, 1), Public, TimeSpan.Zero));
        Assert.False(p.OnAck(new ProbeMessage(Hash, 1, 5, 1), Public, TimeSpan.Zero));
        Assert.Equal(PeerPathState.Probing, p.State);
    }

    [Fact]
    public void A_probe_is_acknowledged_to_its_source_and_the_source_becomes_a_candidate()
    {
        var t = TimeSpan.Zero;
        var p = NewPath(t, Public);
        var unexpected = Ep("198.51.100.77", 4000);
        var ack = p.OnProbe(new ProbeMessage(Hash, 1, 0, 9), unexpected, t);
        Assert.NotNull(ack);
        var decoded = P2PProtocol.DecodeProbe(ack)!;
        Assert.Equal(P2PMessageKind.ProbeAck, P2PProtocol.KindOf(ack));
        Assert.Equal((Hash, (ushort)0, (ushort)1, 9u), (decoded.MatchHash, decoded.FromIndex, decoded.ToIndex, decoded.Sequence));
        Assert.Contains(unexpected, p.Candidates);
        Assert.Equal(PeerPathState.Probing, p.State);   // a probe proves only their direction
        Assert.Null(p.OnProbe(new ProbeMessage(Hash, 7, 0, 9), unexpected, t));
    }

    [Fact]
    public void Without_candidates_nothing_is_sent_and_the_clock_does_not_run()
    {
        var t = TimeSpan.Zero;
        var p = new PeerPath(0, 1, Hash, Options, t);
        for (var now = t; now < TimeSpan.FromSeconds(60); now += TimeSpan.FromSeconds(1)) Assert.Empty(Sent(p, now));
        Assert.Equal(PeerPathState.Probing, p.State);
        // The peer registers a minute in: probing starts now and has the whole timeout from here.
        p.AddCandidates([Public]);
        var late = TimeSpan.FromSeconds(60);
        Assert.Single(Sent(p, late));
        Sent(p, late + Options.PunchTimeout - TimeSpan.FromMilliseconds(100));
        Assert.Equal(PeerPathState.Probing, p.State);
        Sent(p, late + Options.PunchTimeout);
        Assert.Equal(PeerPathState.Failed, p.State);
    }

    [Fact]
    public void No_answer_within_the_timeout_fails()
    {
        var t = TimeSpan.Zero;
        var p = NewPath(t, Public);
        for (var now = t; now < Options.PunchTimeout; now += TimeSpan.FromMilliseconds(100)) Sent(p, now);
        Assert.Equal(PeerPathState.Probing, p.State);
        Assert.Empty(Sent(p, Options.PunchTimeout));
        Assert.Equal(PeerPathState.Failed, p.State);
        Assert.Empty(Sent(p, Options.PunchTimeout + TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Open_path_sends_keepalives_at_the_interval_and_quiet_reopens_probing()
    {
        var t = TimeSpan.Zero;
        var p = NewPath(t, Public);
        Sent(p, t);
        p.OnAck(new ProbeMessage(Hash, 1, 0, 1), Public, t);
        Assert.Empty(Sent(p, t + TimeSpan.FromMilliseconds(999)));
        var keep = Sent(p, t + TimeSpan.FromSeconds(1));
        Assert.Single(keep);
        Assert.Equal(Public, keep[0].To);
        Assert.Equal(P2PMessageKind.KeepAlive, P2PProtocol.KindOf(keep[0].Datagram));
        Assert.Empty(Sent(p, t + TimeSpan.FromSeconds(1.5)));

        // Hearing the peer (keepalive or game traffic) pushes the deadline; silence past it reopens probing.
        Assert.True(p.OnKeepAlive(new KeepAliveMessage(Hash, 1), Public, t + TimeSpan.FromSeconds(10)));
        p.Heard(t + TimeSpan.FromSeconds(20));
        Sent(p, t + TimeSpan.FromSeconds(49));
        Assert.Equal(PeerPathState.Open, p.State);
        Sent(p, t + TimeSpan.FromSeconds(50));
        Assert.Equal(PeerPathState.Probing, p.State);
        Assert.Null(p.Live);
        Assert.Single(Sent(p, t + TimeSpan.FromSeconds(50)));   // probing resumed at once, to the same candidate
    }

    [Fact]
    public void A_spectator_given_its_index_by_the_rendezvous_probes_and_answers_as_that_index()
    {
        // The game connected as 8888; the rendezvous made this node 8889 (the node sets it before adding the candidates).
        var t = TimeSpan.Zero;
        var p = new PeerPath(myIndex: 8888, peerIndex: 0, Hash, Options, t) { MyIndex = 8889 };
        p.AddCandidates([Public]);
        var probe = P2PProtocol.DecodeProbe(Assert.Single(Sent(p, t)).Datagram)!;
        Assert.Equal(((ushort)8889, (ushort)0), (probe.FromIndex, probe.ToIndex));

        Assert.Null(p.OnProbe(new ProbeMessage(Hash, 0, 8888, 1), Public, t));      // the host probing the game's index: not this node
        var ack = P2PProtocol.DecodeProbe(p.OnProbe(new ProbeMessage(Hash, 0, 8889, 1), Public, t)!)!;
        Assert.Equal(((ushort)8889, (ushort)0), (ack.FromIndex, ack.ToIndex));
        Assert.True(p.OnAck(new ProbeMessage(Hash, 0, 8889, 1), Public, t));
        var keepAlive = P2PProtocol.DecodeKeepAlive(Assert.Single(Sent(p, t + Options.KeepAliveInterval)).Datagram)!;
        Assert.Equal((ushort)8889, keepAlive.FromIndex);
    }
}
