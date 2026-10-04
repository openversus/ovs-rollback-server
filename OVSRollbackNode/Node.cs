// Node.cs
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using OVS.Rollback.Common;
using OVS.Rollback.Configuration;
using OVS.Rollback.Core;
using OVS.Rollback.Models;
using OVS.Rollback.P2P;
using OVS.Rollback.Utils;

namespace OVS.Rollback.Node
{
    /// <summary>
    /// One UDP socket, shared three ways: the local game talks to it as its rollback server (over loopback), peers'
    /// nodes talk to it with the P2P protocol and, once a path is open, with game traffic, and the rendezvous
    /// answers registrations on it. The game's first NewConnection names the match; the match config names the
    /// host; the host's node runs the engine for everyone, every other node forwards its game to the host.
    /// </summary>
    internal sealed class Node : IAsyncDisposable
    {
        private enum Role { Unknown, Host, Forwarder }

        private enum Phase
        {
            /// <summary>Fetching the match config to learn the role.</summary>
            Resolving,
            /// <summary>
            /// Registering with the rendezvous and probing peers. The local game's datagrams are dropped meanwhile
            /// (it resends NewConnection every ~22 ms for 45 s), so that the fallback decision, when it comes, is
            /// the same for every node: a host whose game were already on the local engine could not follow its
            /// guest to the relay.
            /// </summary>
            Punching,
            /// <summary>Host: every expected path open, serving. Forwarder: forwarding to the host's open path, or to the relay.</summary>
            Serving,
            /// <summary>No path and no relay: nothing can be done for this match.</summary>
            Stuck
        }

        private sealed class Session(string matchId, string key, ushort myIndex, TimeSpan now)
        {
            public readonly string MatchId = matchId;
            public readonly string Key = key;
            public readonly ushort MyIndex = myIndex;
            public readonly ulong Hash = P2PProtocol.HashMatch(matchId, key);
            public readonly TimeSpan Started = now;
            public Phase Phase = Phase.Resolving;
            public Role Role = Role.Unknown;
            public ushort HostIndex = PeersMessage.NoHost;
            public OVSMatchConfig? Config;
            public Task<OVSMatchConfig?>? ConfigFetch;
            public readonly Dictionary<ushort, PeerPath> Peers = [];
            public HashSet<ushort> Expected = [];
            public IPEndPoint? GameEndPoint;
            public IPEndPoint? Target;          // forwarder: where the game's datagrams go
            public bool TargetIsRelay;
            public IPEndPoint? Relay;
            public Task<IPEndPoint?>? RelayLookup;
            public IPEndPoint? PublicEndPoint;
            public TimeSpan NextRegister;
            public TimeSpan LastGame = now;
            public bool PeersComplete;
            public bool GameDisconnecting;
            public bool HeardFromTarget;        // forwarder: a game datagram has come back from its target
            public bool NotifyFallback;         // was the host and fell back: tell the peers on open paths
            public TimeSpan NoticesUntil, NextNotice;
        }

        private readonly ILogger _log;
        private readonly ServerConfiguration _config;
        private readonly NodeSettings _settings;
        private ushort _port;
        private readonly Socket _socket;
        private readonly RollbackServer _engine;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly PuncherOptions _puncher;
        private readonly IPEndPoint? _rendezvous;
        private readonly IPEndPoint? _relay;
        private readonly byte[] _buffer = new byte[2048];
        private Session? _session;
        private IReadOnlyList<IPEndPoint> _localCandidates = [];
        /// <summary>The watchdog token the parent's keepalives must carry; null when there is no watchdog.</summary>
        private readonly ulong? _parentToken;
        private TimeSpan _lastParent;
        /// <summary>Set when the node decides to stop (the parent is gone); the loop ends and RunAsync returns.</summary>
        private string? _stopReason;

        public Node(ILogger logger, ServerConfiguration config, ushort port)
        {
            _log = logger;
            _config = config;
            _settings = config.Node;
            _port = port;
            _puncher = new PuncherOptions(
                TimeSpan.FromMilliseconds(Math.Max(20, _settings.ProbeIntervalMilliseconds)),
                TimeSpan.FromSeconds(Math.Max(1, _settings.PunchTimeoutSeconds)),
                TimeSpan.FromMilliseconds(Math.Max(100, _settings.KeepAliveIntervalMilliseconds)),
                TimeSpan.FromSeconds(Math.Max(5, _settings.PeerTimeoutSeconds)));
            _rendezvous = LocalCandidates.Parse(_settings.Rendezvous);
            _relay = LocalCandidates.Parse(_settings.RelayFallback);
            if (!string.IsNullOrWhiteSpace(_settings.Rendezvous) && _rendezvous is null)
                _log.LogError("Node.Rendezvous {Value} does not resolve; P2P is off until it does", _settings.Rendezvous);
            if (!string.IsNullOrWhiteSpace(_settings.RelayFallback) && _relay is null)
                _log.LogError("Node.RelayFallback {Value} does not resolve; there is no fallback", _settings.RelayFallback);
            if (!string.IsNullOrWhiteSpace(_settings.ParentToken))
            {
                // A token that cannot be read is a misconfiguration, not "no watchdog": a node that outlives its
                // game is the failure the watchdog exists for, so refuse to start rather than run without it.
                if (!ulong.TryParse(_settings.ParentToken.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var token))
                    throw new ArgumentException($"Node.ParentToken \"{_settings.ParentToken}\" is not an unsigned 64-bit number");
                _parentToken = token;
                _lastParent = _clock.Elapsed;
            }

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            UdpSockets.IgnoreConnectionReset(_socket);
            SocketConfigurator.ConfigureForLowLatency(_socket, _log);
            // Port 0 asks for any free port (the mod reports the one taken through Node.PortFile and /identify).
            try
            {
                _socket.Bind(new IPEndPoint(IPAddress.Any, _port));
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                // The fixed port is taken: take any free one. The rendezvous and the relay only ever see the
                // public mapping, so the port is nobody's business but the game's, which is told what the client
                // reported at /identify (from Node.PortFile), or the fixed port by a client that reports none.
                _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                _log.LogWarning("UDP {Fixed} is in use; listening on {Port} instead. A client that does not report its node's port will not find this node.",
                    port, ((IPEndPoint)_socket.LocalEndPoint!).Port);
            }
            _port = (ushort)((IPEndPoint)_socket.LocalEndPoint!).Port;
            _engine = new RollbackServer(Utilities.NewLogger<RollbackServer>(), _socket, config.Server.MaxPlayers);
            // The server tells the cloud a match started through its authenticated status events; a node has no
            // such key, so it posts the key-checked route the TS server has for this instead.
            _engine.MatchStarted += (matchId, key) => _ = Singletons.SharedHTTPHelper.PostMatchKeyedAsync(Constants.Endpoints.OVSMatchStarted, matchId, key);
        }

        public async Task RunAsync(CancellationToken ct)
        {
            _engine.Start();
            _localCandidates = LocalCandidates.Discover(_port);
            _log.LogInformation("Node listening on UDP {Port}; rendezvous {Rendezvous}; relay {Relay}; LAN candidates: {Lan}",
                _port, _rendezvous?.ToString() ?? "none", _relay?.ToString() ?? "none", string.Join(", ", _localCandidates));
            _log.LogInformation("Waiting for the game to connect to 127.0.0.1:{Port}", _port);
            if (_parentToken is not null)
            {
                _log.LogInformation("Watchdog on: exiting after {Seconds} s without the parent's keepalive", ParentTimeout.TotalSeconds);
            }
            WritePortFile();

            await Task.Factory.StartNew(() => Loop(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            if (_stopReason is not null)
            {
                _log.LogInformation("Node stopping: {Reason}", _stopReason);
            }
        }

        private TimeSpan ParentTimeout => TimeSpan.FromSeconds(Math.Max(2, _settings.ParentTimeoutSeconds));

        /// <summary>
        /// Writes the bound port to Node.PortFile, whole or not at all (written beside it, then renamed over it),
        /// so a reader never sees a half-written number. A failure is logged and the node runs on: the mod then
        /// reports no port, and the server names the fixed one.
        /// </summary>
        private void WritePortFile()
        {
            if (string.IsNullOrWhiteSpace(_settings.PortFile)) return;
            string path = Path.GetFullPath(_settings.PortFile);
            string temp = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(temp, _port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
                File.Move(temp, path, overwrite: true);
                _log.LogInformation("Port {Port} written to {Path}", _port, path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.LogError("Could not write the port file {Path}: {Error}", path, e.Message);
                try { File.Delete(temp); } catch (Exception) { }
            }
        }

        private void Loop(CancellationToken ct)
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            EndPoint from = any;
            while (!ct.IsCancellationRequested && _stopReason is null)
            {
                // Poll instead of a receive timeout: no exception per idle interval.
                if (_socket.Poll(10_000, SelectMode.SelectRead))
                {
                    int n;
                    try
                    {
                        from = any;
                        n = _socket.ReceiveFrom(_buffer, ref from);
                    }
                    catch (SocketException e)
                    {
                        _log.LogDebug("Receive failed: {Error}", e.SocketErrorCode);
                        continue;
                    }
                    try
                    {
                        OnDatagram(_buffer, n, (IPEndPoint)from, _clock.Elapsed);
                    }
                    catch (Exception e)
                    {
                        _log.LogError(e, "Handling a datagram from {From} failed", from);
                    }
                }
                try
                {
                    Tick(_clock.Elapsed);
                }
                catch (Exception e)
                {
                    _log.LogError(e, "Tick failed");
                }
            }
        }

        // ── Datagrams ──

        private void OnDatagram(byte[] buffer, int length, IPEndPoint from, TimeSpan now)
        {
            var data = buffer.AsSpan(0, length);
            if (P2PProtocol.IsP2P(data))
            {
                OnP2P(data, from, now);
            }
            else if (IPAddress.IsLoopback(from.Address))
            {
                OnGameDatagram(buffer, length, from, now);
            }
            else
            {
                OnRemoteDatagram(buffer, length, from, now);
            }
        }

        /// <summary>From the local game. A NewConnection names the match (and may start a new session).</summary>
        private void OnGameDatagram(byte[] buffer, int length, IPEndPoint from, TimeSpan now)
        {
            byte[] decompressed;
            try { decompressed = CompressionHelper.Decompress(buffer.AsSpan(0, length).ToArray()); }
            catch (Exception) { decompressed = buffer.AsSpan(0, length).ToArray(); }
            if (decompressed.Length == 0) return;

            var type = (ClientMessageType)decompressed[0];
            if (type == ClientMessageType.NewConnection)
            {
                var parsed = MessageSerializer.ParseClientMessage(decompressed);
                if (parsed?.Payload is not NewConnectionPayload nc || string.IsNullOrEmpty(nc.MatchData.MatchId))
                {
                    _log.LogWarning("Unparseable NewConnection ({Bytes} bytes) from the game at {From}", length, from);
                    return;
                }
                if (_session is null || _session.MatchId != nc.MatchData.MatchId)
                {
                    StartSession(nc, from, now);
                }
            }

            var s = _session;
            if (s is null) return;
            s.LastGame = now;
            s.GameEndPoint = from;
            if (type == ClientMessageType.Disconnecting && !s.GameDisconnecting)
            {
                s.GameDisconnecting = true;
                _log.LogInformation("Match {Match}: the game is leaving", s.MatchId);
            }

            switch (s.Role)
            {
                case Role.Host when s.Phase == Phase.Serving:
                    _engine.Deliver(buffer, length, from);
                    break;
                case Role.Forwarder when s.Target is not null:
                    Send(buffer, length, s.Target);
                    break;
                default:
                    // Not ready: the game resends NewConnection every ~22 ms, and nothing else is sent before the reply.
                    break;
            }
        }

        /// <summary>From a peer's node (game traffic over an open path) or anything else on the internet.</summary>
        private void OnRemoteDatagram(byte[] buffer, int length, IPEndPoint from, TimeSpan now)
        {
            var s = _session;
            if (s is null) return;
            switch (s.Role)
            {
                case Role.Host when s.Phase == Phase.Serving:
                    foreach (var path in s.Peers.Values)
                    {
                        if (from.Equals(path.Live)) { path.Heard(now); break; }
                    }
                    // The engine checks the match id and key in the NewConnection and knows later senders by address.
                    _engine.Deliver(buffer, length, from);
                    break;
                case Role.Forwarder when s.Target is not null && from.Equals(s.Target):
                    s.HeardFromTarget = true;
                    if (!s.TargetIsRelay && s.Peers.TryGetValue(s.HostIndex, out var host)) host.Heard(now);
                    if (s.GameEndPoint is not null) Send(buffer, length, s.GameEndPoint);
                    break;
                default:
                    break;
            }
        }

        private void OnP2P(ReadOnlySpan<byte> data, IPEndPoint from, TimeSpan now)
        {
            var s = _session;
            switch (P2PProtocol.KindOf(data))
            {
                case P2PMessageKind.Peers:
                    if (s is not null && _rendezvous is not null && from.Equals(_rendezvous) && P2PProtocol.DecodePeers(data) is { } peers)
                    {
                        OnPeers(s, peers, now);
                    }
                    break;

                case P2PMessageKind.Probe:
                    if (s is not null && P2PProtocol.DecodeProbe(data) is { } probe && s.Peers.TryGetValue(probe.FromIndex, out var probed))
                    {
                        var ack = probed.OnProbe(probe, from, now);
                        if (ack is not null) Send(ack, ack.Length, from);
                    }
                    break;

                case P2PMessageKind.ProbeAck:
                    if (s is not null && P2PProtocol.DecodeProbe(data) is { } acked && s.Peers.TryGetValue(acked.FromIndex, out var path))
                    {
                        bool wasOpen = path.State == PeerPathState.Open;
                        if (path.OnAck(acked, from, now) && !wasOpen)
                        {
                            _log.LogInformation("Match {Match}: path to player {Peer} open at {Live} after {Elapsed:F1} s",
                                s.MatchId, path.PeerIndex, path.Live, (now - s.Started).TotalSeconds);
                            if (s.Role == Role.Forwarder && path.PeerIndex == s.HostIndex) UseTarget(s, path.Live!, relay: false);
                            if (s.Role == Role.Host && s.Phase == Phase.Punching && s.Expected.All(i => s.Peers[i].State == PeerPathState.Open))
                            {
                                s.Phase = Phase.Serving;
                                _log.LogInformation("Match {Match}: every path is open; the engine serves this match", s.MatchId);
                                // The cloud holds the players' "your server is ready" until the host actually serves.
                                _ = Singletons.SharedHTTPHelper.PostMatchKeyedAsync(Constants.Endpoints.OVSP2PReady, s.MatchId, s.Key);
                            }
                        }
                    }
                    break;

                case P2PMessageKind.Fallback:
                    // Only from the host, on the path this node opened to it, for this match.
                    if (s is not null && s.Role == Role.Forwarder && !s.TargetIsRelay && P2PProtocol.DecodeFallback(data) is { } fallback
                        && fallback.MatchHash == s.Hash && fallback.FromIndex == s.HostIndex
                        && s.Peers.TryGetValue(s.HostIndex, out var hostPath) && from.Equals(hostPath.Live))
                    {
                        FallBack(s, "the host's node fell back to the relay");
                    }
                    break;

                case P2PMessageKind.KeepAlive:
                    if (s is not null && P2PProtocol.DecodeKeepAlive(data) is { } alive && s.Peers.TryGetValue(alive.FromIndex, out var kept))
                    {
                        kept.OnKeepAlive(alive, from, now);
                    }
                    break;

                case P2PMessageKind.Register:
                    // Only the rendezvous receives these.
                    break;

                case P2PMessageKind.Parent:
                    // Only from this machine, and only with this launch's token: anything else is noise.
                    if (_parentToken is { } expected && IPAddress.IsLoopback(from.Address) && P2PProtocol.DecodeParent(data) is { } parent && parent.Token == expected)
                    {
                        _lastParent = now;
                    }
                    break;
            }
        }

        // ── Session lifecycle ──

        private void StartSession(NewConnectionPayload nc, IPEndPoint game, TimeSpan now)
        {
            var old = _session;
            if (old is not null)
            {
                _log.LogInformation("Match {Old}: replaced by match {New} (role was {Role}, phase {Phase})", old.MatchId, nc.MatchData.MatchId, old.Role, old.Phase);
            }
            var s = new Session(nc.MatchData.MatchId, nc.MatchData.Key, nc.PlayerData.PlayerIndex, now) { GameEndPoint = game };
            _session = s;
            _log.LogInformation("Match {Match}: the game at {Game} is player {Index}; fetching the match config", s.MatchId, game, s.MyIndex);
            s.ConfigFetch = Singletons.SharedHTTPHelper.FetchMatchConfigAsync(s.MatchId, s.Key);
        }

        private void Tick(TimeSpan now)
        {
            if (_parentToken is not null && now - _lastParent > ParentTimeout)
            {
                _stopReason = $"nothing from the parent process for {ParentTimeout.TotalSeconds:F0} s; the game is gone";
                return;
            }

            var s = _session;
            if (s is null) return;

            if (now - s.LastGame > TimeSpan.FromSeconds(Math.Max(10, _settings.GameTimeoutSeconds)))
            {
                _log.LogInformation("Match {Match}: nothing from the game for {Seconds} s; forgetting it", s.MatchId, _settings.GameTimeoutSeconds);
                _session = null;
                return;
            }

            switch (s.Phase)
            {
                case Phase.Resolving:
                    if (s.ConfigFetch is { IsCompleted: true } fetch)
                    {
                        s.ConfigFetch = null;
                        Resolve(s, fetch.IsCompletedSuccessfully ? fetch.Result : null, now);
                    }
                    break;

                case Phase.Punching:
                case Phase.Serving:
                    if (_rendezvous is not null && !s.PeersComplete && now >= s.NextRegister)
                    {
                        s.NextRegister = now + TimeSpan.FromMilliseconds(Math.Max(100, _settings.RegisterIntervalMilliseconds));
                        var register = P2PProtocol.Encode(new RegisterMessage(s.MatchId, s.Key, s.MyIndex, s.Role == Role.Host, _localCandidates));
                        Send(register, register.Length, _rendezvous);
                    }
                    foreach (var path in s.Peers.Values)
                    {
                        var before = path.State;
                        foreach (var (to, datagram) in path.Tick(now)) Send(datagram, datagram.Length, to);
                        if (path.State != before) OnPathChanged(s, path, now);
                    }
                    if (s.Phase == Phase.Punching && now - s.Started >= TimeSpan.FromSeconds(Math.Max(5, _settings.PunchDeadlineSeconds)))
                    {
                        // A peer that never registered (its game never connected) has no candidates and so no path
                        // timeout; the game that connected here has been waiting since s.Started, out of its 45 s.
                        FallBack(s, $"not every peer registered within {_settings.PunchDeadlineSeconds} s");
                    }
                    else if (s.Role == Role.Forwarder && !s.TargetIsRelay && s.Target is not null && !s.HeardFromTarget
                        && now - s.Started >= TimeSpan.FromSeconds(Math.Max(5, _settings.PunchDeadlineSeconds) + Math.Max(0, _settings.ForwarderGraceSeconds)))
                    {
                        // The path to the host opened, but the host's node never answered the game: it fell back (and
                        // its notice was lost) or it died. The whole match has to be on one side.
                        FallBack(s, $"the host's node has not answered the game in {_settings.PunchDeadlineSeconds + _settings.ForwarderGraceSeconds} s");
                    }
                    if (s.NotifyFallback)
                    {
                        // A few notices over a second: a single datagram can be lost.
                        s.NotifyFallback = false;
                        s.NoticesUntil = now + TimeSpan.FromSeconds(1);
                        s.NextNotice = now;
                    }
                    if (now < s.NoticesUntil && now >= s.NextNotice)
                    {
                        s.NextNotice = now + TimeSpan.FromMilliseconds(250);
                        var notice = P2PProtocol.Encode(new FallbackMessage(s.Hash, s.MyIndex));
                        foreach (var path in s.Peers.Values)
                        {
                            if (path.Live is { } live) Send(notice, notice.Length, live);
                        }
                    }
                    break;

                case Phase.Stuck:
                    break;
            }

            if (s.RelayLookup is { IsCompleted: true } lookup && s.Target is null)
            {
                s.RelayLookup = null;
                var relay = lookup.IsCompletedSuccessfully ? lookup.Result : null;
                if (relay is null)
                {
                    _log.LogError("Match {Match}: the server named no relay; the game will time out", s.MatchId);
                    s.Phase = Phase.Stuck;
                }
                else
                {
                    s.Relay = relay;
                    UseTarget(s, relay, relay: true);
                }
            }
        }

        private void Resolve(Session s, OVSMatchConfig? config, TimeSpan now)
        {
            if (config is null)
            {
                _log.LogError("Match {Match}: no match config from {Url}; the game will time out", s.MatchId, Singletons.SharedHTTPHelper.RegisterURL);
                s.Phase = Phase.Stuck;
                return;
            }
            s.Config = config;

            var humans = config.Players.Where(p => !p.IsBot).ToList();
            var host = humans.FirstOrDefault(p => p.IsHost && !p.IsSpectator && p.PlayerIndex < 8888)
                ?? humans.Where(p => !p.IsSpectator && p.PlayerIndex < 8888).OrderBy(p => p.PlayerIndex).FirstOrDefault();
            if (host is null)
            {
                _log.LogError("Match {Match}: the config names no human player to host", s.MatchId);
                s.Phase = Phase.Stuck;
                return;
            }
            s.HostIndex = host.PlayerIndex;
            s.Role = host.PlayerIndex == s.MyIndex ? Role.Host : Role.Forwarder;
            s.Expected = s.Role == Role.Host
                ? humans.Where(p => p.PlayerIndex != s.MyIndex).Select(p => p.PlayerIndex).ToHashSet()
                : [s.HostIndex];
            _log.LogInformation("Match {Match}: {Players} human player(s), host is player {Host}; this node is the {Role}",
                s.MatchId, humans.Count, s.HostIndex, s.Role == Role.Host ? "host" : "forwarder");

            if (s.Role == Role.Host)
            {
                _engine.PreloadMatchConfig(s.MatchId, config);
            }

            if (s.Role == Role.Host && s.Expected.Count == 0)
            {
                // The only human (one player against bots): no peer to punch to or wait for, so neither a rendezvous nor a
                // relay is needed, with or without one configured. Without this the host waits for a probe ack that never
                // comes and falls back at the punch deadline.
                s.PeersComplete = true;
                s.Phase = Phase.Serving;
                _log.LogInformation("Match {Match}: no other human player; the engine serves this match at once", s.MatchId);
                _ = Singletons.SharedHTTPHelper.PostMatchKeyedAsync(Constants.Endpoints.OVSP2PReady, s.MatchId, s.Key);
                return;
            }

            if (_rendezvous is null)
            {
                // No P2P with other players: everyone goes to the relay, host included (the relay is the engine for this match).
                s.Role = Role.Forwarder;
                s.Phase = Phase.Punching;
                FallBack(s, "no rendezvous configured");
                return;
            }

            foreach (var index in s.Expected)
            {
                s.Peers[index] = new PeerPath(s.MyIndex, index, s.Hash, _puncher, now);
            }
            s.Phase = Phase.Punching;
            s.NextRegister = now;
        }

        private void OnPeers(Session s, PeersMessage peers, TimeSpan now)
        {
            if (!peers.Accepted)
            {
                _log.LogError("Match {Match}: the rendezvous rejected this node's registration (wrong key?)", s.MatchId);
                if (s.Role == Role.Forwarder && s.Target is null) FallBack(s, "rendezvous rejected the registration");
                return;
            }
            if (s.PublicEndPoint is null || !s.PublicEndPoint.Equals(peers.YourPublicEndPoint))
            {
                s.PublicEndPoint = peers.YourPublicEndPoint;
                _log.LogInformation("Match {Match}: this node's public address is {Public}", s.MatchId, s.PublicEndPoint);
            }
            if (peers.Relay is not null) s.Relay = peers.Relay;

            foreach (var peer in peers.Peers)
            {
                if (!s.Peers.TryGetValue(peer.PlayerIndex, out var path)) continue;
                bool had = path.HasCandidates;
                path.AddCandidates([peer.PublicEndPoint, .. peer.LocalCandidates]);
                if (!had)
                {
                    _log.LogInformation("Match {Match}: player {Peer}{Host} is at {Public} (LAN: {Lan}); probing",
                        s.MatchId, peer.PlayerIndex, peer.IsHost ? " (host)" : "", peer.PublicEndPoint, string.Join(", ", peer.LocalCandidates));
                }
            }
            if (!s.PeersComplete && s.Expected.All(i => s.Peers[i].HasCandidates))
            {
                s.PeersComplete = true;
                _log.LogInformation("Match {Match}: every expected peer is known ({Count})", s.MatchId, s.Expected.Count);
            }
        }

        private void OnPathChanged(Session s, PeerPath path, TimeSpan now)
        {
            switch (path.State)
            {
                case PeerPathState.Failed:
                    _log.LogWarning("Match {Match}: no path to player {Peer} after {Seconds:F0} s ({Candidates} candidate(s) tried)",
                        s.MatchId, path.PeerIndex, _puncher.PunchTimeout.TotalSeconds, path.Candidates.Count);
                    // Before anything serves: the whole match goes to the relay, on every node, since each sees the
                    // same failure. A path lost mid-match is only logged; the game's own 45 s timeout decides.
                    if (s.Phase == Phase.Punching) FallBack(s, $"player {path.PeerIndex} unreachable");
                    break;
                case PeerPathState.Probing:
                    _log.LogWarning("Match {Match}: the path to player {Peer} went quiet; probing again", s.MatchId, path.PeerIndex);
                    break;
            }
        }

        private void UseTarget(Session s, IPEndPoint target, bool relay)
        {
            s.Target = target;
            s.TargetIsRelay = relay;
            s.Phase = Phase.Serving;
            _log.LogInformation("Match {Match}: forwarding the game to {Target}{Kind}", s.MatchId, target, relay ? " (relay)" : " (host's node)");
        }

        private void FallBack(Session s, string reason)
        {
            if (s.RelayLookup is not null) return;      // already asked
            if (s.Role == Role.Host)
            {
                // The peers that reached this node must follow it, or they wait on a host that no longer hosts.
                s.NotifyFallback = true;
                _log.LogWarning("Match {Match}: telling the peers on open paths that the match goes to the relay", s.MatchId);
            }
            var relay = s.Relay ?? _relay;
            if (relay is not null)
            {
                _log.LogWarning("Match {Match}: {Reason}; falling back to the configured relay at {Relay}{Was}", s.MatchId, reason, relay,
                    s.Role == Role.Host ? " (this node would have hosted)" : "");
                s.Role = Role.Forwarder;
                UseTarget(s, relay, relay: true);
                return;
            }
            // The server deploys the relay for this match on the first report and answers with its address.
            _log.LogWarning("Match {Match}: {Reason}; asking the server for a relay{Was}", s.MatchId, reason,
                s.Role == Role.Host ? " (this node would have hosted)" : "");
            s.Role = Role.Forwarder;
            // A forwarder leaving the host's node: no target until the answer comes (the lookup is applied only to a
            // session without one), so the game's datagrams wait as they do while punching.
            s.Target = null;
            s.TargetIsRelay = false;
            s.RelayLookup = LookUpRelayAsync(s.MatchId, s.Key);
        }

        private async Task<IPEndPoint?> LookUpRelayAsync(string matchId, string key)
        {
            string body = await Singletons.SharedHTTPHelper.PostMatchKeyedForBodyAsync(Constants.Endpoints.OVSP2PFailed, matchId, key);
            if (string.IsNullOrWhiteSpace(body)) return null;
            try
            {
                var answer = System.Text.Json.JsonSerializer.Deserialize(body, OVSJsonContext.Default.P2PRelayResponse);
                if (answer is null || answer.Port <= 0 || string.IsNullOrWhiteSpace(answer.Host)) return null;
                return LocalCandidates.Parse($"{answer.Host}:{answer.Port}");
            }
            catch (System.Text.Json.JsonException e)
            {
                _log.LogError("Match {Match}: the relay answer could not be read: {Error}; body {Body}", matchId, e.Message, body);
                return null;
            }
        }

        private void Send(byte[] data, int length, IPEndPoint to)
        {
            try
            {
                _socket.SendTo(data, 0, length, SocketFlags.None, to);
            }
            catch (SocketException e)
            {
                _log.LogDebug("Send to {To} failed: {Error}", to, e.SocketErrorCode);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _engine.DisposeAsync();
            _socket.Dispose();
        }
    }
}
