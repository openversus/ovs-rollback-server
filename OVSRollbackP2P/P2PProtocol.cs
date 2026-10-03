// P2PProtocol.cs
using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace OVS.Rollback.P2P
{
    /// <summary>
    /// The messages nodes exchange with the rendezvous and with each other. They share the game's socket, so
    /// every one starts with <see cref="Magic"/>: a game datagram can never begin with it, because the game's
    /// second byte is a message type (1..12 after the bitmask compression, where the first byte is the mask and
    /// the second the type), and the magic's second byte is 'V' (86).
    /// </summary>
    public enum P2PMessageKind : byte
    {
        Register = 1,
        Peers = 2,
        Probe = 3,
        ProbeAck = 4,
        KeepAlive = 5
    }

    /// <summary>One address a peer may be reached at: its public mapping as the rendezvous saw it, or a LAN address.</summary>
    public readonly record struct Candidate(IPEndPoint EndPoint)
    {
        public override string ToString() => EndPoint.ToString();
    }

    /// <summary>A node telling the rendezvous where it is, for one match.</summary>
    public sealed record RegisterMessage(string MatchId, string MatchKey, ushort PlayerIndex, bool IsHost, IReadOnlyList<IPEndPoint> LocalCandidates);

    /// <summary>What the rendezvous knows about one node of a match.</summary>
    public sealed record PeerInfo(ushort PlayerIndex, bool IsHost, IPEndPoint PublicEndPoint, IReadOnlyList<IPEndPoint> LocalCandidates);

    /// <summary>The rendezvous's answer to a registration: the registrant's own public mapping and every other node it knows.</summary>
    public sealed record PeersMessage(bool Accepted, IPEndPoint YourPublicEndPoint, ushort HostIndex, IPEndPoint? Relay, IReadOnlyList<PeerInfo> Peers)
    {
        public const ushort NoHost = ushort.MaxValue;
    }

    /// <summary>A probe between two nodes (and its acknowledgement): which match, from whom, to whom.</summary>
    public sealed record ProbeMessage(ulong MatchHash, ushort FromIndex, ushort ToIndex, uint Sequence);

    public sealed record KeepAliveMessage(ulong MatchHash, ushort FromIndex);

    public static class P2PProtocol
    {
        /// <summary>'O' 'V' 'S' 'P' '2' 'P', then the version, then the kind.</summary>
        public static ReadOnlySpan<byte> Magic => "OVSP2P"u8;
        public const byte Version = 1;
        public const int HeaderLength = 8;
        public const int MaxDatagram = 1024;
        private const int MaxCandidates = 16;
        private const int MaxPeers = 16;

        /// <summary>True when the datagram is one of ours (so not the game's).</summary>
        public static bool IsP2P(ReadOnlySpan<byte> datagram) =>
            datagram.Length >= HeaderLength && datagram[..Magic.Length].SequenceEqual(Magic) && datagram[Magic.Length] == Version;

        public static P2PMessageKind KindOf(ReadOnlySpan<byte> datagram) => (P2PMessageKind)datagram[HeaderLength - 1];

        /// <summary>
        /// FNV-1a of the match id and key: what probes and keepalives carry instead of either. A probe for the match
        /// can only be built by a node that knows both, which is what the game's NewConnection carries.
        /// </summary>
        public static ulong HashMatch(string matchId, string matchKey)
        {
            ulong hash = 14695981039346656037UL;
            foreach (byte b in Encoding.UTF8.GetBytes(matchId + "\0" + matchKey))
            {
                hash ^= b;
                hash *= 1099511628211UL;
            }
            return hash;
        }

        // ── Encoding ──

        public static byte[] Encode(RegisterMessage m)
        {
            var w = new Writer(P2PMessageKind.Register);
            w.String(m.MatchId);
            w.String(m.MatchKey);
            w.U16(m.PlayerIndex);
            w.U8((byte)(m.IsHost ? 1 : 0));
            w.EndPoints(m.LocalCandidates);
            return w.ToArray();
        }

        public static byte[] Encode(PeersMessage m)
        {
            var w = new Writer(P2PMessageKind.Peers);
            w.U8((byte)(m.Accepted ? 0 : 1));
            w.EndPoint(m.YourPublicEndPoint);
            w.U16(m.HostIndex);
            w.EndPoint(m.Relay ?? new IPEndPoint(IPAddress.Any, 0));
            if (m.Peers.Count > MaxPeers) throw new ArgumentException($"{m.Peers.Count} peers; at most {MaxPeers}");
            w.U8((byte)m.Peers.Count);
            foreach (var p in m.Peers)
            {
                w.U16(p.PlayerIndex);
                w.U8((byte)(p.IsHost ? 1 : 0));
                w.EndPoint(p.PublicEndPoint);
                w.EndPoints(p.LocalCandidates);
            }
            return w.ToArray();
        }

        public static byte[] Encode(ProbeMessage m, bool ack)
        {
            var w = new Writer(ack ? P2PMessageKind.ProbeAck : P2PMessageKind.Probe);
            w.U64(m.MatchHash);
            w.U16(m.FromIndex);
            w.U16(m.ToIndex);
            w.U32(m.Sequence);
            return w.ToArray();
        }

        public static byte[] Encode(KeepAliveMessage m)
        {
            var w = new Writer(P2PMessageKind.KeepAlive);
            w.U64(m.MatchHash);
            w.U16(m.FromIndex);
            return w.ToArray();
        }

        // ── Decoding: null for anything short or malformed ──

        public static RegisterMessage? DecodeRegister(ReadOnlySpan<byte> d)
        {
            if (!IsP2P(d) || KindOf(d) != P2PMessageKind.Register) return null;
            var r = new Reader(d[HeaderLength..]);
            if (!r.String(out var matchId) || !r.String(out var key) || !r.U16(out var index) || !r.U8(out var flags) || !r.EndPoints(out var locals)) return null;
            return new RegisterMessage(matchId, key, index, (flags & 1) != 0, locals);
        }

        public static PeersMessage? DecodePeers(ReadOnlySpan<byte> d)
        {
            if (!IsP2P(d) || KindOf(d) != P2PMessageKind.Peers) return null;
            var r = new Reader(d[HeaderLength..]);
            if (!r.U8(out var status) || !r.EndPoint(out var mine) || !r.U16(out var host) || !r.EndPoint(out var relay) || !r.U8(out var count)) return null;
            if (count > MaxPeers) return null;
            var peers = new List<PeerInfo>(count);
            for (int i = 0; i < count; i++)
            {
                if (!r.U16(out var index) || !r.U8(out var flags) || !r.EndPoint(out var pub) || !r.EndPoints(out var locals)) return null;
                peers.Add(new PeerInfo(index, (flags & 1) != 0, pub, locals));
            }
            return new PeersMessage(status == 0, mine, host, relay.Port == 0 ? null : relay, peers);
        }

        public static ProbeMessage? DecodeProbe(ReadOnlySpan<byte> d)
        {
            if (!IsP2P(d) || KindOf(d) is not (P2PMessageKind.Probe or P2PMessageKind.ProbeAck)) return null;
            var r = new Reader(d[HeaderLength..]);
            if (!r.U64(out var hash) || !r.U16(out var from) || !r.U16(out var to) || !r.U32(out var seq)) return null;
            return new ProbeMessage(hash, from, to, seq);
        }

        public static KeepAliveMessage? DecodeKeepAlive(ReadOnlySpan<byte> d)
        {
            if (!IsP2P(d) || KindOf(d) != P2PMessageKind.KeepAlive) return null;
            var r = new Reader(d[HeaderLength..]);
            if (!r.U64(out var hash) || !r.U16(out var from)) return null;
            return new KeepAliveMessage(hash, from);
        }

        // ── Primitives (little-endian; strings u16-length UTF-8; endpoints IPv4 + u16 port) ──

        private sealed class Writer
        {
            private readonly MemoryStream _s = new();

            public Writer(P2PMessageKind kind)
            {
                _s.Write(Magic);
                _s.WriteByte(Version);
                _s.WriteByte((byte)kind);
            }

            public void U8(byte v) => _s.WriteByte(v);
            public void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); _s.Write(b); }
            public void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); _s.Write(b); }
            public void U64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); _s.Write(b); }

            public void String(string v)
            {
                var bytes = Encoding.UTF8.GetBytes(v);
                if (bytes.Length > ushort.MaxValue) throw new ArgumentException("string too long");
                U16((ushort)bytes.Length);
                _s.Write(bytes);
            }

            public void EndPoint(IPEndPoint ep)
            {
                var ip = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
                if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) throw new ArgumentException($"IPv4 only: {ep}");
                _s.Write(ip.GetAddressBytes());
                U16((ushort)ep.Port);
            }

            public void EndPoints(IReadOnlyList<IPEndPoint> eps)
            {
                if (eps.Count > MaxCandidates) throw new ArgumentException($"{eps.Count} candidates; at most {MaxCandidates}");
                U8((byte)eps.Count);
                foreach (var ep in eps) EndPoint(ep);
            }

            public byte[] ToArray()
            {
                if (_s.Length > MaxDatagram) throw new InvalidOperationException($"{_s.Length} bytes; a datagram holds {MaxDatagram}");
                return _s.ToArray();
            }
        }

        private ref struct Reader(ReadOnlySpan<byte> data)
        {
            private readonly ReadOnlySpan<byte> _d = data;
            private int _o = 0;

            private bool Has(int n) => _d.Length - _o >= n;

            public bool U8(out byte v) { v = 0; if (!Has(1)) return false; v = _d[_o++]; return true; }
            public bool U16(out ushort v) { v = 0; if (!Has(2)) return false; v = BinaryPrimitives.ReadUInt16LittleEndian(_d[_o..]); _o += 2; return true; }
            public bool U32(out uint v) { v = 0; if (!Has(4)) return false; v = BinaryPrimitives.ReadUInt32LittleEndian(_d[_o..]); _o += 4; return true; }
            public bool U64(out ulong v) { v = 0; if (!Has(8)) return false; v = BinaryPrimitives.ReadUInt64LittleEndian(_d[_o..]); _o += 8; return true; }

            public bool String(out string v)
            {
                v = "";
                if (!U16(out var len) || !Has(len)) return false;
                v = Encoding.UTF8.GetString(_d.Slice(_o, len));
                _o += len;
                return true;
            }

            public bool EndPoint(out IPEndPoint ep)
            {
                ep = null!;
                if (!Has(6)) return false;
                var ip = new IPAddress(_d.Slice(_o, 4));
                _o += 4;
                if (!U16(out var port)) return false;
                ep = new IPEndPoint(ip, port);
                return true;
            }

            public bool EndPoints(out IReadOnlyList<IPEndPoint> eps)
            {
                eps = [];
                if (!U8(out var count) || count > MaxCandidates) return false;
                var list = new List<IPEndPoint>(count);
                for (int i = 0; i < count; i++)
                {
                    if (!EndPoint(out var ep)) return false;
                    list.Add(ep);
                }
                eps = list;
                return true;
            }
        }
    }
}
