// PeerPath.cs
using System.Net;

namespace OVS.Rollback.P2P
{
    public enum PeerPathState
    {
        /// <summary>Probing every candidate; nothing has answered yet.</summary>
        Probing,
        /// <summary>A probe was acknowledged: <see cref="PeerPath.Live"/> is the address that answered.</summary>
        Open,
        /// <summary>No candidate answered within the timeout.</summary>
        Failed
    }

    public sealed record PuncherOptions(TimeSpan ProbeInterval, TimeSpan PunchTimeout, TimeSpan KeepAliveInterval, TimeSpan PeerTimeout);

    /// <summary>
    /// The hole punch to one peer, driven by time passed in (so it runs the same in tests as on a socket).
    /// Both sides send probes to every address they know for the other (the public mapping the rendezvous saw,
    /// plus LAN addresses); each probe that arrives is acknowledged to wherever it came from. The first
    /// acknowledgement to arrive proves the path both ways, and its source address is the one to use. A probe
    /// that arrives from an address nobody announced (a NAT that rewrites ports) adds that address as a candidate.
    /// Once open, keepalives keep the NAT mapping alive; silence for PeerTimeout reopens probing.
    /// </summary>
    public sealed class PeerPath
    {
        private readonly PuncherOptions _o;
        private readonly List<IPEndPoint> _candidates = [];
        private TimeSpan _started, _nextProbe, _nextKeepAlive, _lastHeard;
        private uint _sequence;

        public ushort MyIndex { get; }
        public ushort PeerIndex { get; }
        public ulong MatchHash { get; }
        public PeerPathState State { get; private set; } = PeerPathState.Probing;
        public IPEndPoint? Live { get; private set; }
        public IReadOnlyList<IPEndPoint> Candidates => _candidates;
        /// <summary>Set when a peer's own public mapping (as the rendezvous saw it) is known and in the candidate list.</summary>
        public bool HasCandidates => _candidates.Count > 0;

        public PeerPath(ushort myIndex, ushort peerIndex, ulong matchHash, PuncherOptions options, TimeSpan now)
        {
            MyIndex = myIndex;
            PeerIndex = peerIndex;
            MatchHash = matchHash;
            _o = options;
            _started = _nextProbe = now;
            _lastHeard = now;
        }

        public void AddCandidates(IEnumerable<IPEndPoint> endPoints)
        {
            foreach (var ep in endPoints)
            {
                if (ep.Port == 0 || ep.Address.Equals(IPAddress.Any)) continue;
                if (!_candidates.Contains(ep)) _candidates.Add(ep);
            }
        }

        /// <summary>What to send now: probes to every candidate while probing, a keepalive on the live path once open.</summary>
        public IEnumerable<(IPEndPoint To, byte[] Datagram)> Tick(TimeSpan now)
        {
            switch (State)
            {
                case PeerPathState.Probing:
                    if (now - _started >= _o.PunchTimeout)
                    {
                        State = PeerPathState.Failed;
                        yield break;
                    }
                    if (now < _nextProbe) yield break;
                    _nextProbe = now + _o.ProbeInterval;
                    _sequence++;
                    foreach (var ep in _candidates)
                    {
                        yield return (ep, P2PProtocol.Encode(new ProbeMessage(MatchHash, MyIndex, PeerIndex, _sequence), ack: false));
                    }
                    break;

                case PeerPathState.Open:
                    if (now - _lastHeard >= _o.PeerTimeout)
                    {
                        // The path went quiet: probe again, from scratch, with the same candidates.
                        State = PeerPathState.Probing;
                        _started = _nextProbe = now;
                        Live = null;
                        yield break;
                    }
                    if (now < _nextKeepAlive) yield break;
                    _nextKeepAlive = now + _o.KeepAliveInterval;
                    yield return (Live!, P2PProtocol.Encode(new KeepAliveMessage(MatchHash, MyIndex)));
                    break;
            }
        }

        private bool IsMine(ProbeMessage m) => m.MatchHash == MatchHash && m.FromIndex == PeerIndex && m.ToIndex == MyIndex;

        /// <summary>A probe from the peer: acknowledge it to where it came from. Returns the ack, or null if it is not this peer's.</summary>
        public byte[]? OnProbe(ProbeMessage m, IPEndPoint from, TimeSpan now)
        {
            if (!IsMine(m)) return null;
            _lastHeard = now;
            AddCandidates([from]);
            return P2PProtocol.Encode(m with { FromIndex = MyIndex, ToIndex = PeerIndex }, ack: true);
        }

        /// <summary>The peer acknowledged one of our probes from <paramref name="from"/>: the path is open there.</summary>
        public bool OnAck(ProbeMessage m, IPEndPoint from, TimeSpan now)
        {
            if (!IsMine(m)) return false;
            _lastHeard = now;
            if (State != PeerPathState.Open)
            {
                State = PeerPathState.Open;
                Live = from;
                _nextKeepAlive = now + _o.KeepAliveInterval;
                _lastHeard = now;
            }
            return true;
        }

        public bool OnKeepAlive(KeepAliveMessage m, IPEndPoint from, TimeSpan now)
        {
            if (m.MatchHash != MatchHash || m.FromIndex != PeerIndex) return false;
            _lastHeard = now;
            return true;
        }

        /// <summary>Game traffic arrived from the live address: as good as a keepalive.</summary>
        public void Heard(TimeSpan now) => _lastHeard = now;
    }
}
