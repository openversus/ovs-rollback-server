// RendezvousRegistry.cs
using System.Net;

namespace OVS.Rollback.P2P
{
    /// <summary>Decides whether a registration's match key is the match's. The bench accepts everything; see <see cref="AcceptAllValidator"/>.</summary>
    public interface IMatchKeyValidator
    {
        ValueTask<bool> IsValidAsync(string matchId, string matchKey, CancellationToken ct);
    }

    /// <summary>
    /// No check against the match store. What still holds: the first node to register a match fixes its key, and
    /// every later registration must present the same one, so a stranger who knows only the match id cannot join
    /// after a real player has.
    /// </summary>
    public sealed class AcceptAllValidator : IMatchKeyValidator
    {
        public ValueTask<bool> IsValidAsync(string matchId, string matchKey, CancellationToken ct) => ValueTask.FromResult(true);
    }

    /// <summary>
    /// Who is where, per match: each node's public mapping as its registration arrived, its LAN candidates, and
    /// whether it is the host. A registration is answered with every other node of the match known so far; nodes
    /// re-register until they have everyone they expect. Entries expire after <see cref="Ttl"/> of silence.
    /// </summary>
    public sealed class RendezvousRegistry(TimeSpan ttl)
    {
        private sealed class Registration(PeerInfo info, TimeSpan seen)
        {
            public PeerInfo Info = info;
            public TimeSpan LastSeen = seen;
        }

        private sealed class Match(string key)
        {
            public readonly string Key = key;
            public readonly Dictionary<ushort, Registration> Nodes = [];
        }

        private readonly Dictionary<string, Match> _matches = [];
        private readonly Lock _lock = new();

        public TimeSpan Ttl { get; } = ttl;
        public int MatchCount { get { lock (_lock) return _matches.Count; } }

        /// <summary>
        /// Records the registration and answers it. <paramref name="from"/> is the address the datagram came from: the node's public mapping.
        /// A player is known by its own index. A spectator (<see cref="P2PProtocol.FirstSpectatorIndex"/> or above) is given one
        /// here, since every game client spectates as 8888 whatever its slot: the lowest spectator index free in the match, kept
        /// for that public mapping (a re-registration from it keeps the index; the index frees when the registration expires).
        /// The answer's <see cref="PeersMessage.YourIndex"/> tells the node which one it has.
        /// </summary>
        public PeersMessage Register(RegisterMessage m, IPEndPoint from, TimeSpan now, IPEndPoint? relay = null)
        {
            lock (_lock)
            {
                if (!_matches.TryGetValue(m.MatchId, out var match))
                {
                    match = new Match(m.MatchKey);
                    _matches[m.MatchId] = match;
                }
                else if (!string.Equals(match.Key, m.MatchKey, StringComparison.Ordinal))
                {
                    return new PeersMessage(false, from, PeersMessage.NoHost, relay, []);
                }

                ushort yours = m.PlayerIndex >= P2PProtocol.FirstSpectatorIndex ? SpectatorIndex(match, from) : m.PlayerIndex;
                var info = new PeerInfo(yours, m.IsHost, from, m.LocalCandidates);
                match.Nodes[yours] = new Registration(info, now);

                ushort host = PeersMessage.NoHost;
                var peers = new List<PeerInfo>();
                foreach (var (index, reg) in match.Nodes)
                {
                    if (reg.Info.IsHost) host = index;
                    if (index != yours) peers.Add(reg.Info);
                }
                return new PeersMessage(true, from, host, relay, peers, yours);
            }
        }

        /// <summary>
        /// The spectator index of the node at <paramref name="from"/>: the one it registered with before, else the lowest free one.
        /// Keyed by the public mapping, the only thing that tells two spectators apart. A NAT that rebinds the mapping mid-match
        /// gets a new index, and the old one stays taken until it expires: an accepted limit, as a node stops registering once
        /// it knows every peer it expects, and its paths are open by then.
        /// </summary>
        private static ushort SpectatorIndex(Match match, IPEndPoint from)
        {
            foreach (var (index, reg) in match.Nodes)
            {
                if (index >= P2PProtocol.FirstSpectatorIndex && reg.Info.PublicEndPoint.Equals(from)) return index;
            }
            ushort free = P2PProtocol.FirstSpectatorIndex;
            while (match.Nodes.ContainsKey(free)) free++;
            return free;
        }

        /// <summary>Drops nodes silent for longer than the TTL, and matches left empty. Returns how many matches remain.</summary>
        public int Expire(TimeSpan now)
        {
            lock (_lock)
            {
                foreach (var (id, match) in _matches.ToList())
                {
                    foreach (var (index, reg) in match.Nodes.ToList())
                    {
                        if (now - reg.LastSeen > Ttl) match.Nodes.Remove(index);
                    }
                    if (match.Nodes.Count == 0) _matches.Remove(id);
                }
                return _matches.Count;
            }
        }
    }
}
