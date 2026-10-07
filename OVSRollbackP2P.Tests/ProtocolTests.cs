using System.Net;
using OVS.Rollback.Core;
using OVS.Rollback.Models;
using OVS.Rollback.P2P;
using Xunit;

namespace OVS.Rollback.P2P.Tests;

public class ProtocolTests
{
    private static IPEndPoint Ep(string ip, int port) => new(IPAddress.Parse(ip), port);

    public static IEnumerable<object[]> Registers() =>
    [
        [new RegisterMessage("6ac0712bdd431596a0080938", "a-key", 0, true, [])],
        [new RegisterMessage("m", "", 8888, false, [Ep("10.0.0.7", 41234)])],
        [new RegisterMessage("Ünïcödé ✓", "kéy", 65535, false, Enumerable.Range(1, 16).Select(i => Ep($"192.168.1.{i}", 1000 + i)).ToList())],
    ];

    [Theory]
    [MemberData(nameof(Registers))]
    public void Register_round_trips(RegisterMessage m)
    {
        var bytes = P2PProtocol.Encode(m);
        Assert.True(P2PProtocol.IsP2P(bytes));
        Assert.Equal(P2PMessageKind.Register, P2PProtocol.KindOf(bytes));
        var back = P2PProtocol.DecodeRegister(bytes);
        Assert.NotNull(back);
        Assert.Equal(m.MatchId, back.MatchId);
        Assert.Equal(m.MatchKey, back.MatchKey);
        Assert.Equal(m.PlayerIndex, back.PlayerIndex);
        Assert.Equal(m.IsHost, back.IsHost);
        Assert.Equal(m.LocalCandidates, back.LocalCandidates);
    }

    [Fact]
    public void Peers_round_trips_with_every_field_non_default()
    {
        var m = new PeersMessage(true, Ep("203.0.113.9", 60001), 2,
            Ep("198.51.100.4", 57000),
            [
                new PeerInfo(2, true, Ep("203.0.113.10", 41234), [Ep("10.1.1.1", 41234), Ep("172.16.0.5", 41234)]),
                new PeerInfo(8889, false, Ep("203.0.113.11", 5), []),
            ]);
        var back = P2PProtocol.DecodePeers(P2PProtocol.Encode(m));
        Assert.NotNull(back);
        Assert.True(back.Accepted);
        Assert.Equal(m.YourPublicEndPoint, back.YourPublicEndPoint);
        Assert.Equal((ushort)2, back.HostIndex);
        Assert.Equal(m.Relay, back.Relay);
        Assert.Equal(2, back.Peers.Count);
        Assert.Equal(m.Peers[0], back.Peers[0] with { LocalCandidates = m.Peers[0].LocalCandidates });
        Assert.Equal(m.Peers[0].LocalCandidates, back.Peers[0].LocalCandidates);
        Assert.Equal(m.Peers[1].PlayerIndex, back.Peers[1].PlayerIndex);
        Assert.False(back.Peers[1].IsHost);
        Assert.Empty(back.Peers[1].LocalCandidates);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(0x8000_0000_0000_0000UL)]
    [InlineData(ulong.MaxValue)]
    public void Parent_round_trips(ulong token)
    {
        var bytes = P2PProtocol.Encode(new ParentMessage(token));
        Assert.True(P2PProtocol.IsP2P(bytes));
        Assert.Equal(P2PMessageKind.Parent, P2PProtocol.KindOf(bytes));
        Assert.Equal(P2PProtocol.HeaderLength + 8, bytes.Length);
        var back = P2PProtocol.DecodeParent(bytes);
        Assert.NotNull(back);
        Assert.Equal(token, back.Token);
    }

    [Fact]
    public void Parent_decoder_rejects_other_kinds_and_a_short_body()
    {
        Assert.Null(P2PProtocol.DecodeParent(P2PProtocol.Encode(new KeepAliveMessage(7, 1))));
        var bytes = P2PProtocol.Encode(new ParentMessage(42));
        Assert.Null(P2PProtocol.DecodeParent(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Null(P2PProtocol.DecodeKeepAlive(bytes));
    }

    [Fact]
    public void Peers_rejection_and_no_relay_round_trip()
    {
        var back = P2PProtocol.DecodePeers(P2PProtocol.Encode(new PeersMessage(false, Ep("1.2.3.4", 1), PeersMessage.NoHost, null, [])));
        Assert.NotNull(back);
        Assert.False(back.Accepted);
        Assert.Null(back.Relay);
        Assert.Equal(PeersMessage.NoHost, back.HostIndex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Probe_round_trips(bool ack)
    {
        var m = new ProbeMessage(0x0123456789abcdefUL, 1, 8888, 77);
        var bytes = P2PProtocol.Encode(m, ack);
        Assert.Equal(ack ? P2PMessageKind.ProbeAck : P2PMessageKind.Probe, P2PProtocol.KindOf(bytes));
        Assert.Equal(m, P2PProtocol.DecodeProbe(bytes));
    }

    [Fact]
    public void Fallback_round_trips_and_is_not_a_keepalive()
    {
        var m = new FallbackMessage(P2PProtocol.HashMatch("6ac0712bdd431596a0080938", "key"), 0);
        var bytes = P2PProtocol.Encode(m);
        Assert.Equal(m, P2PProtocol.DecodeFallback(bytes));
        Assert.Equal(P2PMessageKind.Fallback, P2PProtocol.KindOf(bytes));
        Assert.Null(P2PProtocol.DecodeKeepAlive(bytes));
        Assert.Null(P2PProtocol.DecodeFallback(P2PProtocol.Encode(new KeepAliveMessage(m.MatchHash, 0))));
    }

    [Fact]
    public void KeepAlive_round_trips()
    {
        var m = new KeepAliveMessage(P2PProtocol.HashMatch("6ac0712bdd431596a0080938", "key"), 3);
        Assert.Equal(m, P2PProtocol.DecodeKeepAlive(P2PProtocol.Encode(m)));
    }

    [Fact]
    public void Every_truncation_decodes_to_null_not_garbage()
    {
        var register = P2PProtocol.Encode(new RegisterMessage("match", "key", 1, false, [Ep("10.0.0.1", 5)]));
        var peers = P2PProtocol.Encode(new PeersMessage(true, Ep("1.1.1.1", 1), 0, null, [new PeerInfo(0, true, Ep("2.2.2.2", 2), [Ep("3.3.3.3", 3)])]));
        var probe = P2PProtocol.Encode(new ProbeMessage(1, 2, 3, 4), ack: false);
        for (int n = 0; n < register.Length; n++) Assert.Null(P2PProtocol.DecodeRegister(register.AsSpan(0, n)));
        for (int n = 0; n < peers.Length; n++) Assert.Null(P2PProtocol.DecodePeers(peers.AsSpan(0, n)));
        for (int n = 0; n < probe.Length; n++) Assert.Null(P2PProtocol.DecodeProbe(probe.AsSpan(0, n)));
    }

    private static PeersMessage SomePeers(ushort? yourIndex) => new(true, Ep("203.0.113.9", 60001), 0, Ep("198.51.100.4", 57000),
        [new PeerInfo(0, true, Ep("203.0.113.10", 41234), [Ep("10.1.1.1", 41234)]), new PeerInfo(8888, false, Ep("203.0.113.11", 5), [])],
        yourIndex);

    [Fact]
    public void Your_index_is_appended_to_the_old_peers_layout()
    {
        // An older node's decoder reads the peers and stops; it never looks for the end. So the new reply must be the old
        // one, byte for byte, plus the index: then an older node reads it exactly as before.
        var old = P2PProtocol.Encode(SomePeers(null));
        var current = P2PProtocol.Encode(SomePeers(8889));
        Assert.Equal(old.Length + 2, current.Length);
        Assert.Equal(old, current[..old.Length]);
        Assert.Equal(new byte[] { 0xb9, 0x22 }, current[old.Length..]);   // 8889, little-endian

        var back = P2PProtocol.DecodePeers(current)!;
        Assert.Equal((ushort)8889, back.YourIndex);
        Assert.Equal(2, back.Peers.Count);
    }

    [Fact]
    public void A_reply_without_your_index_decodes_with_none()
    {
        // An older rendezvous: the node keeps the index its game connected with.
        var back = P2PProtocol.DecodePeers(P2PProtocol.Encode(SomePeers(null)))!;
        Assert.Null(back.YourIndex);
        Assert.Equal(2, back.Peers.Count);
        Assert.Null(P2PProtocol.DecodePeers(P2PProtocol.Encode(new PeersMessage(false, Ep("1.2.3.4", 1), PeersMessage.NoHost, null, [])))!.YourIndex);
    }

    [Fact]
    public void Your_index_cut_short_is_rejected_and_bytes_after_it_are_ignored()
    {
        var current = P2PProtocol.Encode(SomePeers(8889));
        Assert.Null(P2PProtocol.DecodePeers(current.AsSpan(0, current.Length - 1)));
        Assert.Null(P2PProtocol.DecodePeers(current.AsSpan(0, current.Length - 2))!.YourIndex);
        Assert.Equal((ushort)8889, P2PProtocol.DecodePeers([.. current, 7, 7, 7])!.YourIndex);
    }

    [Fact]
    public void Kinds_do_not_decode_as_each_other()
    {
        var probe = P2PProtocol.Encode(new ProbeMessage(1, 2, 3, 4), ack: false);
        Assert.Null(P2PProtocol.DecodeRegister(probe));
        Assert.Null(P2PProtocol.DecodePeers(probe));
        Assert.Null(P2PProtocol.DecodeKeepAlive(probe));
    }

    [Fact]
    public void Another_protocol_version_is_not_ours()
    {
        var probe = P2PProtocol.Encode(new ProbeMessage(1, 2, 3, 4), ack: false);
        Assert.True(P2PProtocol.IsP2P(probe));
        probe[P2PProtocol.Magic.Length] = (byte)(P2PProtocol.Version + 1);
        Assert.False(P2PProtocol.IsP2P(probe));
        Assert.Null(P2PProtocol.DecodeProbe(probe));
    }

    [Fact]
    public void Hash_is_stable_and_distinguishes_ids_and_keys()
    {
        Assert.Equal(P2PProtocol.HashMatch("abc", "k"), P2PProtocol.HashMatch("abc", "k"));
        Assert.NotEqual(P2PProtocol.HashMatch("abc", "k"), P2PProtocol.HashMatch("abd", "k"));
        Assert.NotEqual(P2PProtocol.HashMatch("abc", "k"), P2PProtocol.HashMatch("abc", "K"));
        Assert.NotEqual(P2PProtocol.HashMatch("ab", "ck"), P2PProtocol.HashMatch("abc", "k"));
        Assert.NotEqual(0UL, P2PProtocol.HashMatch("", ""));
    }

    // ── The demux: nothing the game or the engine sends may look like a P2P message ──

    private static byte[] ClientMessage(ClientMessageType type, uint sequence, params byte[] payload)
    {
        var raw = new byte[5 + payload.Length];
        raw[0] = (byte)type;
        BitConverter.TryWriteBytes(raw.AsSpan(1), sequence);
        payload.CopyTo(raw, 5);
        return raw;
    }

    [Fact]
    public void Client_messages_compressed_or_raw_are_never_p2p()
    {
        var samples = new List<byte[]>
        {
            ClientMessage(ClientMessageType.NewConnection, 0, 21, 0, 0, 0, 0, 0),
            ClientMessage(ClientMessageType.Input, 0x50325356, 1, 0, 0, 0),              // a sequence spelling "VS2P" after the type
            ClientMessage(ClientMessageType.PlayerInputAck, 0x32505356, 2),
            ClientMessage(ClientMessageType.MatchResult, uint.MaxValue, 2, 0, 0, 0, 0, 1),
            ClientMessage(ClientMessageType.QualityData, 7, 1, 0, 0, 0),
            ClientMessage(ClientMessageType.Disconnecting, 8, 1),
            ClientMessage(ClientMessageType.ReadyToStartMatch, 9, 1),
            ClientMessage(ClientMessageType.PlayerDisconnectedAck, 10, 0),
        };
        foreach (var raw in samples)
        {
            Assert.False(P2PProtocol.IsP2P(raw), $"raw {raw[0]}");
            Assert.False(P2PProtocol.IsP2P(CompressionHelper.Compress(raw)), $"compressed {raw[0]}");
        }
    }

    [Fact]
    public void Server_messages_are_never_p2p()
    {
        var header = new ServerHeader { Type = ServerMessageType.NewConnectionReply, Sequence = 0x50325356 };
        object?[] payloads =
        [
            new NewConnectionReplyPayload { MatchNumPlayers = 2, MatchDurationInFrames = 36000 },
            null,
            new RequestQualityDataPayload { Ping = 40 },
            new PlayersConfigurationDataPayload { NumPlayers = 2 },
            new KickPayload { Reason = 1 },
            new ChangePortPayload { Port = 41235 },
        ];
        ServerMessageType[] types = [ServerMessageType.NewConnectionReply, ServerMessageType.StartGame, ServerMessageType.RequestQualityData,
            ServerMessageType.PlayersConfigurationData, ServerMessageType.Kick, ServerMessageType.ChangePort];
        for (int i = 0; i < types.Length; i++)
        {
            var raw = MessageSerializer.SerializeServerMessage(header with { Type = types[i] }, payloads[i], 2);
            Assert.False(P2PProtocol.IsP2P(raw));
            Assert.False(P2PProtocol.IsP2P(CompressionHelper.Compress(raw)));
        }
    }

    [Fact]
    public void Any_datagram_whose_second_byte_is_a_game_type_is_not_p2p()
    {
        // After the bitmask compression the second byte of every game datagram is its type (1..12); the magic's
        // second byte is 'V'. Random first bytes and random tails: the second byte alone decides.
        var rng = new Random(1234);
        var buf = new byte[64];
        for (int i = 0; i < 20_000; i++)
        {
            rng.NextBytes(buf);
            buf[1] = (byte)rng.Next(1, 13);
            Assert.False(P2PProtocol.IsP2P(buf));
        }
    }
}
