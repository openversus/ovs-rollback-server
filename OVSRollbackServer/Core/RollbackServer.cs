// RollbackServer.cs
using Microsoft.Extensions.Logging;
using OVS.Rollback.Common;
using OVS.Rollback.Configuration;
using OVS.Rollback.Core;
using OVS.Rollback.Models;
using OVS.Rollback.Utils;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Text.Json;
using static OVS.Rollback.Core.Constants;
using static OVS.Rollback.Core.LoggerTemplates;

namespace OVS.Rollback.Core
{
    public sealed partial class RollbackServer : IAsyncDisposable
    {
        // Configuration-driven constants (updated from config at runtime)
        private float TargetFrameTime => 1000f / ServerConfiguration.Instance.Performance.TargetFrameRate;
        private float PingAlpha => ServerConfiguration.Instance.RiftCalculation.PingAlpha;
        private float RiftAlpha => ServerConfiguration.Instance.RiftCalculation.RiftAlpha;
        private byte MaxInputsPerFrame => ServerConfiguration.Instance.GameLogic.MaxInputsPerFrame;
        private int DisconnectTimeout => ServerConfiguration.Instance.GameLogic.DisconnectTimeoutSeconds;

        private readonly ushort _port;
        private readonly int _maxPlayers;
        private readonly Socket _socket;
        private readonly object _sendLock = new();
        private readonly HttpClient _httpClient;
        private readonly HTTPHelper _httpHelper;
        private readonly ILogger<RollbackServer> _logger;

        private readonly ConcurrentDictionary<string, MatchState> _matches = new();
        private readonly ConcurrentDictionary<string, PlayerInfo> _players = new();
        private readonly SemaphoreSlim _matchCreationLock = new(1, 1);
        private ConcurrentBag<string> connections = new();

        // ── Lifecycle ──
        private volatile bool _running;
        private Task? _udpTask;

        // ── Configuration ──
        public string BaseUrl { get; private set; } = "";
        public bool IsOVS { get; private set; }
        public bool IsMVSI { get; private set; }

        // ═══════════════════════════════════════════
        //  Constructor / Lifecycle
        // ═══════════════════════════════════════════

        public RollbackServer(
            ILogger<RollbackServer> logger,
            ushort port = Constants.GameServerPort,
            int maxPlayers = Constants.MaxPlayers)
        {
            _logger = logger;
            //_httpHelper = new HTTPHelper(_logger);
            _httpHelper = Singletons.SharedHTTPHelper;
            _port = port;
            _maxPlayers = maxPlayers;
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            // Initialize HttpClient with configured timeout
            var config = ServerConfiguration.Instance;
            _httpClient = new HttpClient {
                Timeout = TimeSpan.FromSeconds(config.Networking.HttpTimeoutSeconds)
            };

            BaseUrl = Utilities.GetBaseUrlFromEnv(_logger) ?? string.Empty;
            IsOVS = Utilities.IsOVS;
            IsMVSI = Utilities.IsMVSI;

            if (BaseUrl.StringIsNullOrEmpty)
            {
                string errorMsg = "No base URL configured. Please set the OVS_SERVER environment variable.";
                _ = Events.SendTerminatingErrorEvent(this, StatusEventArgs.CreateNew(
                        description: "ConfigurationError",
                        matchEvent: "TerminatingError",
                        matchDescription: errorMsg,
                        exception: new InvalidOperationException(errorMsg)
                        )
                    );
            }

            string serverType = IsOVS ? "OVS" : (IsMVSI ? "MVSI" : "Unknown");
            Log.ServerStarted(_logger, serverType, _port);
        }

        public void Start()
        {
            if (_running)
            {
                return;
            }

            _running = true;

            
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

            SocketConfigurator.ConfigureForLowLatency(_socket, _logger);

            // ← NEW: Apply low-latency socket options (DSCP EF, buffers, DontFragment)
            _socket.Bind(new IPEndPoint(IPAddress.Any, _port));
            _udpTask = Task.Run(RunUdpServerAsync);

            _ = Events.SendServerListeningEvent(this, StatusEventArgs.CreateNew(
                     description: "ServerListening",
                     matchEvent: "ServerListening",
                     matchDescription: $"OVS rollback server has started listening on {_port}"
                     )
                );
            Log.Listening(_logger, _port);
            Log.MatchEndpoint(_logger, BaseUrl);
        }

        public async Task StopAsync()
        {
            if (!_running) return;
            _running = false;

            try {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch { }

            _socket.Close();

            _ = Events.SendServerStopEvent(this, StatusEventArgs.CreateNew(
                description: "ServerStopping",
                matchEvent: "ServerStopping",
                matchDescription: "ServerStopping"
                )
            );

            if (_udpTask is not null)
            {
                try {
                    await _udpTask;
                }
                catch (OperationCanceledException) { }
            }

            Log.ServerStopped(_logger);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _socket.Dispose();
            _httpClient.Dispose();
            _matchCreationLock.Dispose();
        }

        // ═══════════════════════════════════════════
        //  UDP Receive Loop
        // ═══════════════════════════════════════════

        private async Task RunUdpServerAsync()
        {
            var config = ServerConfiguration.Instance;
            var buffer = new byte[1024];
            var anyEp = new IPEndPoint(IPAddress.Any, 0);

            while (_running)
            {
                try
                {
                    var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, anyEp);
                    var remote = (IPEndPoint)result.RemoteEndPoint;

                    if (!connections.Contains(remote.Address.ToString()))
                    {
                        connections.Add(remote.Address.ToString());
                        Log.ConnectionReceived(_logger, remote.Address.ToString());
                    }

                    ServerMetrics.PacketsReceived.Add(1);

                    int receivedBytes = result.ReceivedBytes;

                    // Decompress into a fresh byte[] that is safe to hand off to any
                    // async path (it is independent of the shared receive buffer).
                    // For non-NewConnection messages the buffer is read synchronously
                    // and we return to ReceiveFromAsync as fast as possible.
                    byte[] decompressed;
                    try { decompressed = CompressionHelper.Decompress(buffer.AsSpan(0, receivedBytes)); }
                    catch (Exception ex)
                    {
                        Log.DecompressionFailed(_logger, ex, receivedBytes, remote.ToString());
                        decompressed = buffer[..receivedBytes];
                    }

                    if (decompressed.Length > 0 &&
                        (ClientMessageType)decompressed[0] == ClientMessageType.NewConnection)
                    {
                        // Offload onto the thread pool — the HTTP fetch inside
                        // HandleNewConnection can take tens to hundreds of milliseconds and
                        // must not block the receive loop.
                        _ = HandleNewConnectionMessage(decompressed, remote);
                    }
                    else
                    {
                        // Fast path: all other message types are synchronous and complete
                        // in microseconds. The buffer is stable until the next await.
                        HandleMessage(decompressed, remote);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted) { break; }
                catch (Exception ex)
                {
                    Log.ReceiveError(_logger, ex);
                    if (!_running) break;
                }
            }
        }

        // ═══════════════════════════════════════════
        //  Message Dispatch
        // ═══════════════════════════════════════════

        /// <summary>
        /// Handles a NewConnection packet. Runs on a thread-pool thread so that the
        /// HTTP fetch inside <see cref="HandleNewConnection"/> does not block the UDP
        /// receive loop. The caller must pass an already-decompressed, independently
        /// owned byte array (not a slice of the shared receive buffer).
        /// </summary>
        private async Task HandleNewConnectionMessage(byte[] decompressed, IPEndPoint remote)
        {
            try
            {
                var clientMsg = MessageSerializer.ParseClientMessage(decompressed);
                if (clientMsg is null)
                {
                    _logger.LogWarning(
                        "ParseClientMessage returned null for NewConnection from {Remote}",
                        remote);
                    return;
                }

                var payload = (NewConnectionPayload)clientMsg.Value.Payload;
                await HandleNewConnection(payload, remote);
            }
            catch (Exception ex)
            {
                Log.HandleError(_logger, ex);
            }
        }

        /// <summary>
        /// Handles all non-NewConnection messages. Called synchronously on the UDP
        /// receive loop thread. <paramref name="decompressed"/> is already decompressed
        /// and owned by the caller (safe to read for the duration of this call).
        /// </summary>
        private void HandleMessage(byte[] decompressed, IPEndPoint remote)
        {
            try
            {
                var clientMsg = MessageSerializer.ParseClientMessage(decompressed);
                if (clientMsg is null)
                {
                    _logger.LogWarning(
                        "ParseClientMessage returned null for {Len} bytes from {Remote} " +
                        "firstByte=0x{FirstByte:X2}",
                        decompressed.Length, remote,
                        decompressed.Length > 0 ? decompressed[0] : 0);
                    return;
                }

                var header = clientMsg.Value.Header;
                var type = header.Type;

                string key = $"{remote.Address}:{remote.Port}";
                if (!_players.TryGetValue(key, out var player) || player is null)
                    return;
                if (!_matches.TryGetValue(player.MatchId, out var match) || match is null)
                    return;

                // Input packets carry frame-keyed data and must never be dropped by
                // the sequence gate. Out-of-order delivery during lag-spike recovery
                // causes burst packets with lower sequence numbers to arrive after a
                // higher-sequence packet, permanently dropping frame history and
                // causing permanent desync. Deduplication for Input is handled by
                // TryAdd on the per-frame input dictionary in HandleClientInput.
                // All other message types are idempotent or time-sensitive (ping,
                // ack, ready, disconnect) and correctly discarded when out of order.
                if (type != ClientMessageType.Input)
                {
                    if (header.Sequence <= player.LastSeqRecv)
                        return;
                    player.LastSeqRecv = header.Sequence;
                }

                if (type == ClientMessageType.QualityData)
                {
                    var qPayload = (QualityDataPayload)clientMsg.Value.Payload;
                    if (player.PendingPings.TryRemove(qPayload.ServerMessageSequenceNumber, out long ts))
                        player.Ping = (short)Stopwatch.GetElapsedTime(ts).TotalMilliseconds;
                }

                switch (type)
                {
                    case ClientMessageType.PlayerInputAck:
                        HandlePlayerInputAck(match, player, (PlayerInputAckPayload)clientMsg.Value.Payload);
                        break;
                    case ClientMessageType.ReadyToStartMatch:
                        HandleReady(match, player, ((ReadyToStartMatchPayload)clientMsg.Value.Payload).Ready == 1);
                        _ = Events.SendPlayerReadyEvent(this, StatusEventArgs.CreateNew(
                                description: "PlayerReady",
                                matchEvent: "PlayerReady",
                                matchDescription: $"Player {player.PlayerId} (name: {player.PlayerName}, character: {player.PlayerCharacter}) at PlayerIndex {player.PlayerIndex} readied-up in match {player.MatchId}",
                                matchKey: match.Key,
                                matchId: match.MatchId,
                                matchNumPlayers: match.Players.Count,
                                matchPlayerId: player.PlayerId,
                                matchPlayerIds: [.. match.Players.Select(p => p.Value.PlayerId)]
                                )
                            );
                        break;
                    case ClientMessageType.Input:
                        HandleClientInput(match, player, (InputPayload)clientMsg.Value.Payload);
                        break;
                    case ClientMessageType.Disconnecting:
                        player.Disconnected = true;
                        ServerMetrics.PlayersDisconnected.Add(1);
                        _ = Events.SendPlayerDisconnectEvent(this, StatusEventArgs.CreateNew(
                                description: "PlayerDisconnect",
                                matchEvent: "PlayerDisconnect",
                                matchDescription: $"Player {player.PlayerId} (name: {player.PlayerName}, character: {player.PlayerCharacter}) at PlayerIndex {player.PlayerIndex} disconnected from match {player.MatchId}",
                                matchKey: match.Key,
                                matchId: match.MatchId,
                                matchNumPlayers: match.Players.Count,
                                matchPlayerId: player.PlayerId,
                                matchPlayerIds: [.. match.Players.Select(p => p.Value.PlayerId)]
                                )
                            );
                        Log.PlayerDisconnecting(_logger, player.PlayerIndex, player.MatchId ?? "Unknown");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.HandleError(_logger, ex);
            }


        }

        // ═══════════════════════════════════════════
        //  Connection & Setup
        // ═══════════════════════════════════════════

        private async Task<PlayerInfo?> HandleNewConnection(
            NewConnectionPayload payload, IPEndPoint remote)
        {
            string key = $"{remote.Address}:{remote.Port}";
            var matchData = payload.MatchData;

            MatchState? match;
            await _matchCreationLock.WaitAsync();
            OVSMatchConfig? config = null;

            try
            {
                if (!_matches.TryGetValue(matchData.MatchId, out match))
                {
                    Log.NewMatch(_logger, matchData.MatchId);

                    config = await _httpHelper.FetchMatchConfigAsync(matchData.MatchId, matchData.Key);
                    if (config is null)
                    {
                        Log.FetchConfigFailed(_logger, matchData.MatchId, new InvalidDataException("Match config is null."));
                        _ = Events.SendTerminatingErrorEvent(this, StatusEventArgs.CreateNew(
                                description: "ConfigurationError",
                                matchEvent: "TerminatingError",
                                matchDescription: $"Failed to fetch match configuration for MatchId {matchData.MatchId}.",
                                matchKey: matchData.Key
                                )
                            );
                        return null;
                    }

                    match = new MatchState {
                        MatchId = matchData.MatchId,
                        Key = matchData.Key,
                        DurationInFrames = config.MatchDuration,
                        TickIntervalMs = 1000f / 60f,
                        CurrentFrame = 0,
                        MaxPlayers = config.MaxPlayers,
                        PingPhaseCount = 0,
                        PingPhaseTotal = 20,
                        SequenceCounter = uint.MaxValue,
                        Config = config,
                        // Size Inputs by team-side slot count (MaxPlayers - NumSpectators).
                        // Why not MaxPlayers: when spectators are included in MaxPlayers,
                        // sizing Inputs by MaxPlayers leaves empty trailing slots that the
                        // needMore loop checks forever, blocking the match.
                        // Why not ActualPlayers (humans only): when bots occupy PlayerIndex
                        // slots between humans, a human at PlayerIndex 2 in a 2-human match
                        // would crash with IndexOutOfRange on Inputs[2].
                        // Team-side count is right: every PlayerIndex 0..(team-side-1) is a
                        // real participant (human or bot); spectators have PlayerIndex 8888
                        // and are filtered separately.
                        Inputs = new(config.MaxPlayers - config.NumSpectators),
                        Workspace = new TickWorkspace(config.MaxPlayers),
                        NumBots = config.NumBots,
                        BotIndices = new HashSet<int>(
                            config.Players.Where(p => p.IsBot).Select(p => (int)p.PlayerIndex)
                        )
                    };

                    if (Statics.FinalLogFile.StringIsNullOrWhiteSpace)
                    {
                        //Utilities.FinalLogFile = Path.Combine(Utilities.LogDir, $"{match.MatchId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.log");
                        var safeMatchID = match.MatchId.StringIsNullOrWhiteSpace ? $"UnknownMatchID_{Guid.NewGuid()}" : match.MatchId;
                        Statics.FinalLogFileName = $"{safeMatchID}_{_port}_{DateTime.UtcNow.ToString("yyyyMMdd_HHmmss")}.log";

                        if (Statics.LogArchivePath.NotNullOrWhiteSpace)
                        {
                            Statics.FinalLogFile = Path.Combine(Statics.LogArchivePath, Statics.FinalLogFileName);
                        }
                        if (_logger?.IsEnabled(LogLevel.Information) == true)
                        {
                            _logger.LogInformation("Set final log file path to: {FinalLogFile}", Statics.FinalLogFile);
                        }
                    }

                    for (int i = 0; i < config.MaxPlayers - config.NumSpectators; i++)
                    {
                        match.Inputs.Add(new ConcurrentDictionary<uint, uint>());
                    }
                    _matches[matchData.MatchId] = match;
                    ServerMetrics.MatchesStarted.Add(1);

                    _ = Events.SendConfigReceivedEvent(this, StatusEventArgs.CreateNew(
                            description: "MatchConfigReceived",
                            matchEvent: "MatchConfigReceived",
                            matchDescription: $"Successfully fetched match configuration for MatchId {matchData.MatchId}.",
                            matchKey: matchData.Key,
                            matchId: matchData.MatchId,
                            matchNumPlayers: config.ActualPlayers,
                            matchPlayerIds: config.Players.Select(p => p.PlayerId).ToArray()
                        )
                    );
                }
            }
            finally { _matchCreationLock.Release(); }

            // Fast path: player already fully registered (retransmit / reconnect packet).
            if (_players.TryGetValue(key, out var existing))
            {
                return existing;
            }

            ushort payloadIndex = payload.PlayerData.PlayerIndex;

            // Resolve player identity from match config.
            string playerID = "Unknown";
            string playerName = "Unknown";
            string playerCharacter = "Unknown";

            if (match.Config != null)
            {
                foreach (OvsPlayer? player in match.Config.Players)
                {
                    if (player?.PlayerIndex == payloadIndex)
                    {
                        playerID = player?.PlayerId ?? "Unknown";
                        playerName = player?.PlayerName ?? "Unknown";
                        playerCharacter = player?.PlayerCharacter ?? "Unknown";
                        break;
                    }
                }
            }

            if (playerID == "Unknown" || playerName == "Unknown" || playerCharacter == "Unknown")
            {
                string errorMsg = $"Player data mismatch for PlayerIndex {payloadIndex} in MatchId {matchData.MatchId}. Received PlayerIndex does not match any player in the match configuration.";
                _logger.LogWarning(
                    "Player data mismatch for PlayerIndex {PlayerIndex} in MatchId {MatchId}. " +
                    "Received PlayerIndex does not match any player in the match configuration. " +
                    "This may indicate a client error. MatchData is: {matchdata}",
                    payloadIndex, matchData.MatchId, JsonSerializer.Serialize(payload));
                _ = Events.SendErrorEvent(this, StatusEventArgs.CreateNew(
                        description: "DataError",
                        matchEvent: "DataError",
                        matchDescription: errorMsg,
                        matchKey: matchData.Key,
                        matchId: matchData.MatchId,
                        matchPlayerId: playerID,
                        exception: new InvalidDataException(errorMsg)
                        )
                    );
            }

            var newPlayer = new PlayerInfo {
                EndPoint = remote,
                MatchId = matchData.MatchId,
                PlayerIndex = payloadIndex,
                PlayerId = playerID,
                PlayerName = playerName,
                PlayerCharacter = playerCharacter,
                // Spectators get a unique sentinel PlayerIndex starting at 8888
                // (8888, 8889, 8890, ...) so multiple specs in one match don't
                // collide on the OvsPlayer lookup.
                IsSpectator = payload.PlayerData.PlayerIndex >= 8888 ? true : false,
                LastSeqRecv = 0,
                LastSeqSent = 0,
                AckedFrames = new List<uint>(new uint[match.MaxPlayers]),
                Ping = 0,
                Ready = payload.PlayerData.PlayerIndex >= 8888 ? true : false,
                LastClientFrame = 0,
                LastInputTimestamp = Stopwatch.GetTimestamp(),
                Rift = 0
            };

            // Atomically claim the player slot. If another task already registered
            // this key (duplicate connection packet), GetOrAdd returns the winner's
            // PlayerInfo instead of ours. In that case skip all side-effects to
            // prevent duplicate join logs, events, and ping-phase starts.
            var registered = match.Players.GetOrAdd(key, newPlayer);
            if (!ReferenceEquals(registered, newPlayer))
            {
                // Lost the race — ensure _players is also up to date and return.
                _players.TryAdd(key, registered);
                return registered;
            }

            _players[key] = newPlayer;
            ServerMetrics.PlayersConnected.Add(1);
            Log.PlayerJoined(_logger, payload.PlayerData.PlayerIndex, newPlayer.PlayerId, newPlayer.PlayerName, newPlayer.PlayerCharacter, matchData.MatchId);
            _ = Events.SendPlayerConnectEvent(this, StatusEventArgs.CreateNew(
                    description: "PlayerConnect",
                    matchEvent: "PlayerConnect",
                    matchDescription: $"Player {newPlayer.PlayerId} (name: {newPlayer.PlayerName}, character: {newPlayer.PlayerCharacter}) joined match {newPlayer.MatchId} at PlayerIndex {newPlayer.PlayerIndex}. Spectator: {newPlayer.IsSpectator}",
                    matchKey: match.Key,
                    matchId: match.MatchId,
                    matchNumPlayers: match.Players.Count,
                    matchPlayerId: newPlayer.PlayerId,
                    matchPlayerIds: match.Players.Select(p => p.Value.PlayerId).ToArray()
                    )
                );

            var reply = new NewConnectionReplyPayload {
                Success = 0,
                MatchNumPlayers = (byte)match.Players.Count,
                PlayerIndex = (byte)newPlayer.PlayerIndex,
                MatchDurationInFrames = match.DurationInFrames,
                IsValidationServerDebugMode = 0
            };
            SendServerMessage(match, newPlayer, ServerMessageType.NewConnectionReply, reply);

            // Bots occupy slots in MaxPlayers but never UDP-connect, so they
            // never increment match.ActualPlayers (which is derived from
            // match.Players runtime dict). Subtract them out of the expected
            // count, otherwise the ready check waits forever.
            // >= instead of == ensures late-arriving retransmits don't silently miss
            // the threshold. TryStartPingPhase() inside StartPingPhase guarantees
            // exactly one start regardless of how many times this branch is taken.
            if (match.ActualPlayers >= match.MaxPlayers - match.NumSpectators - match.NumBots)
            {
                StartPingPhase(match);
            }

            return newPlayer;
        }

        // ═══════════════════════════════════════════
        //  Ping Phase (optimized with Timer)
        // ═══════════════════════════════════════════

        private void StartPingPhase(MatchState match)
        {
            if (!match.TryStartPingPhase())
            {
                return;
            }

            var config = ServerConfiguration.Instance;

            Log.PingPhaseStarted(_logger, match.MatchId);
            _ = Events.SendPingPhaseEvent(this, StatusEventArgs.CreateNew(
                    description: "PingPhaseStarted",
                    matchEvent: "PingPhaseStarted",
                    matchDescription: $"Ping phase started for match {match.MatchId} with {match.Players.Count} players.",
                    matchKey: match.Key,
                    matchId: match.MatchId,
                    matchNumPlayers: match.Players.Count,
                    matchPlayerIds: match.Players.Select(p => p.Value.PlayerId).ToArray()
                    )
                );

            uint count = 0;
            Timer? timer = null;
            timer = new Timer(_ => {
                if (count >= config.PingPhase.TotalPings || !_running)
                {
                    // Stop the timer first so no further callbacks are queued
                    // before we broadcast. Change() with Timeout.Infinite is
                    // synchronous-safe: it prevents new ticks but does not
                    // block on an already-running callback the way Dispose(WaitHandle) would.
                    timer?.Change(Timeout.Infinite, Timeout.Infinite);
                    timer?.Dispose();
                    BroadcastPlayersConfiguration(match);
                    return;
                }

                BroadcastRequestQuality(match);
                match.PingPhaseCount = ++count;
            }, null, 0, config.PingPhase.PingIntervalMilliseconds);

            // Store timer to prevent GC
            match.PingPhaseTimer = timer;

        }
        private void BroadcastRequestQuality(MatchState match)
        {
            long ts = Stopwatch.GetTimestamp();
            foreach (var kvp in match.Players)
            {
                var player = kvp.Value;
                if (player.Disconnected) continue;
                var payload = new RequestQualityDataPayload { Ping = player.Ping };
                uint seq = SendServerMessage(match, player, ServerMessageType.RequestQualityData, payload);
                player.PendingPings[seq] = ts;
            }
        }
        private void BroadcastPlayersConfiguration(MatchState match)
        {
            // Exactly one broadcast per match lifetime. Guards against the timer
            // disposal race and any other call site that might be added in future.
            if (!match.TryBroadcastPlayersConfiguration())
            {
                return;
            }

            //ReadOnlySpan<ushort> mapping = [0, 256, 513, 769];
            ReadOnlySpan<ushort> mapping = [0, 255, 512, 768, 1024, 1280, 1536, 1792];
            //int count = match.Players.Count;

            //ushort[] mappingArray = new ushort[match.Players.Count];
            //for (int i = 0; i < match.Players.Count; i++)
            //{
            //    mappingArray[i] = (ushort)((i % 4) * 256);
            //}
            //ReadOnlySpan<ushort> mapping = mappingArray;

            //foreach (var _ in match.Players) count++;

            foreach (var kvp in match.Players)
            {
                var player = kvp.Value;
                if (player.Disconnected)
                {
                    continue;
                }

                var configValues = new List<ushort>(match.MaxPlayers);
                for (int i = 0; i < match.MaxPlayers; i++)
                {
                    configValues.Add(mapping[i % mapping.Length]);
                }

                var payload = new PlayersConfigurationDataPayload {
                    //NumPlayers = (byte)count,
                    NumPlayers = (byte)match.Players.Count,
                    ConfigValues = configValues
                };
                SendServerMessage(match, player, ServerMessageType.PlayersConfigurationData, payload);
            }
        }

        // ═══════════════════════════════════════════
        //  Input & Acknowledgement Handlers (unchanged logic)
        // ═══════════════════════════════════════════

        private void HandlePlayerInputAck(MatchState match, PlayerInfo player, PlayerInputAckPayload payload
        )
        {
            var config = ServerConfiguration.Instance;

            lock (player.Lock)
            {
                for (int i = 0; i < payload.AckFrame.Count && i < player.AckedFrames.Count; i++)
                {
                    uint acked = payload.AckFrame[i];
                    if (acked != 0 && player.AckedFrames[i] < acked)
                        player.AckedFrames[i] = acked;
                }

                if (player.PendingPings.TryRemove(payload.ServerMessageSequenceNumber, out long ts))
                {
                    short newPing = (short)Math.Min(
                        Stopwatch.GetElapsedTime(ts).TotalMilliseconds, 255);

                    if (newPing > -1)
                    {
                        if (!player.PingInitialized)
                        {
                            player.SmoothedPing = newPing;
                            player.PingInitialized = true;
                        }
                        else
                        {
                            player.SmoothedPing = PlayerInfo.ClampFloat(
                                PingAlpha * newPing + (1f - PingAlpha) * player.SmoothedPing, 255f);
                        }

                        player.Ping = newPing;
                        player.HasNewPing = true;
                    }
                }
            }


        }

        private void HandleReady(MatchState match, PlayerInfo player, bool isReady)
        {
            player.Ready = isReady;

            if (player.IsSpectator)
            {
                player.Ready = true; // Spectators are always ready
            }

            // ← CHANGED: loop instead of .All() LINQ
            bool allReady = true;
            foreach (var kvp in match.Players)
            {
                if (!kvp.Value.Ready) { allReady = false; break; }
            }

            if (allReady)
            {
                foreach (var kvp in match.Players)
                {
                    SendServerMessage(match, kvp.Value, ServerMessageType.StartGame, null);
                }

                _ = Events.SendAllPlayersReadyEvent(this, StatusEventArgs.CreateNew(
                        description: "AllPlayersReady",
                        matchEvent: "AllPlayersReady",
                        matchDescription: $"All players are ready in match {match.MatchId}. Starting game.",
                        matchKey: match.Key,
                        matchId: match.MatchId,
                        matchNumPlayers: match.Players.Count,
                        matchPlayerIds: match.Players.Select(p => p.Value.PlayerId).ToArray()
                        )
                    );

                if (!match.IsTickRunning)
                {
                    StartTickLoop(match);
                }

                _ = Events.SendMatchStartEvent(this, StatusEventArgs.CreateNew(
                        description: "MatchStarted",
                        matchEvent: "MatchStarted",
                        matchDescription: $"Match {match.MatchId} started with {match.Players.Count} players.",
                        matchKey: match.Key,
                        matchId: match.MatchId,
                        matchNumPlayers: match.Players.Count,
                        matchPlayerIds: match.Players.Select(p => p.Value.PlayerId).ToArray()
                        )
                    );
            }
        }

        private void HandleClientInput(MatchState match, PlayerInfo player, InputPayload payload)
        {
            if (player.IsSpectator)
            {
                return; // Spectators don't send inputssf
            }

            lock (player.Lock)
            {
                player.LastClientFrame = payload.ClientFrame;
                player.HasNewFrame = true;
                player.LastInputTimestamp = Stopwatch.GetTimestamp();
                player.Disconnected = false;
            }

            var histMap = match.Inputs[player.PlayerIndex];
            for (byte i = 0; i < payload.NumFrames && i < payload.InputPerFrame.Count; i++)
            {
                uint f = payload.StartFrame + i;
                histMap.TryAdd(f, payload.InputPerFrame[i]);
            }

            // ── Buffer per-frame checksums sent by this client ──
            var config = ServerConfiguration.Instance;
            if (config.DesyncDetection.EnableDesyncDetection)
            {
                for (byte i = 0; i < payload.NumChecksums && i < payload.ChecksumPerFrame.Count; i++)
                {
                    uint f = payload.StartFrame + i;
                    uint checksum = payload.ChecksumPerFrame[i];

                    // Per-player fast lookup (for cleanup)
                    player.Checksums.TryAdd(f, checksum);

                    // Cross-player map: frame → { playerIndex → checksum }
                    var frameMap = match.FrameChecksums.GetOrAdd(
                        f, _ => new ConcurrentDictionary<int, uint>());
                    frameMap.TryAdd(player.PlayerIndex, checksum);
                }

                ProcessChecksums(match, player);
            }
        }

        /// <summary>
        /// Inspects every frame in <see cref="MatchState.FrameChecksums"/> that now has a
        /// checksum from every active (non-spectator, non-bot) player, compares the values,
        /// and takes the appropriate action:
        /// <list type="bullet">
        ///   <item>Agreement → advance <see cref="MatchState.LastVerifiedFrame"/> so the tick
        ///     loop can echo it back to clients as <c>ChecksumAckFrame</c>.</item>
        ///   <item>Disagreement → log the desync, increment the offending player's
        ///     <see cref="PlayerInfo.DesyncCount"/>, and kick them if
        ///     <see cref="DesyncDetectionSettings.MaxDesyncCount"/> is exceeded.</item>
        /// </list>
        /// Called from <see cref="HandleClientInput"/> on the thread-pool thread that owns
        /// the incoming packet — never on the hot tick loop.
        /// </summary>
        private void ProcessChecksums(MatchState match, PlayerInfo triggeringPlayer)
        {
            var config = ServerConfiguration.Instance.DesyncDetection;

            // Count how many non-spectator, non-bot, connected players are expected to report.
            // Disconnected players will never send further checksums, so excluding them
            // prevents LastVerifiedFrame (and LastHandledChecksumFrame) from stalling
            // indefinitely when a player drops mid-match.
            int expectedPlayers = 0;
            foreach (var kvp in match.Players)
            {
                if (!kvp.Value.IsSpectator && !kvp.Value.Disconnected &&
                    !match.BotIndices.Contains(kvp.Value.PlayerIndex))
                    expectedPlayers++;
            }

            if (expectedPlayers < 2) return; // Nothing to compare with a single active participant.

            foreach (var frameEntry in match.FrameChecksums)
            {
                uint frame = frameEntry.Key;
                var frameMap = frameEntry.Value;

                // Only evaluate once all expected players have reported.
                if (frameMap.Count < expectedPlayers) continue;

                // Skip frames already fully handled (verified or desynced).
                if (frame <= match.LastHandledChecksumFrame) continue;

                // ── Majority-vote: find the checksum held by the most players ──
                // Frequency map: checksum value → count of players reporting it.
                // Heap allocation is acceptable here; ProcessChecksums runs off the
                // hot tick path (called from the UDP receive handler thread-pool).
                var freq = new Dictionary<uint, int>(frameMap.Count);
                foreach (var entry in frameMap)
                {
                    freq.TryGetValue(entry.Value, out int c);
                    freq[entry.Value] = c + 1;
                }

                // Find the top vote count, then check whether multiple checksum
                // groups share it. A tie is always the case in a 2-player match
                // (1-1) and can occur in larger lobbies with even splits (2-2, etc.).
                int maxCount = 0;
                foreach (var kv in freq)
                    if (kv.Value > maxCount) maxCount = kv.Value;

                int tiedGroupCount = 0;
                foreach (var kv in freq)
                    if (kv.Value == maxCount) tiedGroupCount++;

                uint trustedChecksum;
                if (tiedGroupCount == 1)
                {
                    // Clear majority — use it directly.
                    trustedChecksum = 0;
                    foreach (var kv in freq)
                    {
                        if (kv.Value == maxCount) { trustedChecksum = kv.Key; break; }
                    }
                }
                else
                {
                    // Tie: 2-player match (always 1-1) or larger even split (2-2, etc.).
                    // Fall back to connection-quality tiebreaking: trust the checksum
                    // reported by the player with the best combined ping + rift score.
                    // A player with a consistently low-latency, low-rift connection is
                    // more likely to have an accurate, stable simulation state.
                    trustedChecksum = ResolveChecksumTie(match, frameMap, freq, maxCount);
                }

                bool anyDesync = maxCount < expectedPlayers;

                if (!anyDesync)
                {
                    // All players agreed — advance the verified frame watermark.
                    match.TryAdvanceVerifiedFrame(frame);
                    match.TryAdvanceHandledChecksumFrame(frame);
                    ServerMetrics.ChecksumsProcessed.Add(1);
                }
                else
                {
                    ServerMetrics.DesyncsDetected.Add(1);

                    // Find the most trusted player on the winning side for log context.
                    PlayerInfo? trustedPlayer = FindBestQualityPlayerForChecksum(
                        match, frameMap, trustedChecksum);

                    // Flag every player whose checksum doesn't match the trusted one.
                    foreach (var entry in frameMap)
                    {
                        if (entry.Value == trustedChecksum) continue;

                        int outlierIndex = entry.Key;
                        uint outlierChecksum = entry.Value;

                        PlayerInfo? outlier = null;
                        foreach (var kvp in match.Players)
                        {
                            if (kvp.Value.PlayerIndex == outlierIndex)
                            {
                                outlier = kvp.Value;
                                break;
                            }
                        }

                        if (outlier is null) continue;

                        Log.DesyncDetected(_logger, frame,
                            (ushort)(trustedPlayer?.PlayerIndex ?? 0), trustedPlayer?.PlayerName, trustedChecksum.ToString("X8"),
                            (ushort)outlierIndex, outlier.PlayerName, outlierChecksum.ToString("X8"));

                        if (outlier.FirstDesyncFrame == 0)
                            outlier.FirstDesyncFrame = frame;

                        outlier.DesyncCount++;

                        if (config.MaxDesyncCount > 0 &&
                            outlier.DesyncCount >= config.MaxDesyncCount)
                        {
                            if (config.KickDesyncingPlayer)
                            {
                                var kickPayload = new KickPayload { Reason = 2 /* desync */, Param1 = frame };
                                SendServerMessage(match, outlier, ServerMessageType.Kick, kickPayload);
                                outlier.Disconnected = true;

                                _logger.LogWarning(
                                    "Kicked player {Index} (name: {PlayerName}) from match {MatchId} after {Count} desyncs " +
                                    "(first at frame {First})",
                                    outlier.PlayerIndex, outlier.PlayerName, match.MatchId,
                                    outlier.DesyncCount, outlier.FirstDesyncFrame);
                            }
                            else
                            {
                                _logger.LogWarning(
                                    "Would kick player {Index} (name: {PlayerName}) from match {MatchId} after {Count} desyncs " +
                                    "(first at frame {First})",
                                    outlier.PlayerIndex, outlier.PlayerName, match.MatchId,
                                    outlier.DesyncCount, outlier.FirstDesyncFrame);
                            }
                        }
                    }

                    // Frame has been fully evaluated (desynced) — mark it so subsequent
                    // ProcessChecksums calls skip it rather than re-logging the same desync.
                    match.TryAdvanceHandledChecksumFrame(frame);
                }
            }
        }

        /// <summary>
        /// Resolves a checksum tie by returning the checksum reported by the player with
        /// the best connection quality among all players in the tied groups.
        ///
        /// Tiebreaker score = <c>PingVariance + RiftVariance × TargetFrameTime²</c> — the
        /// sum of the two population variances accumulated via Welford's algorithm over
        /// the whole match. Lower = more stable connection and simulation over time.
        /// Falls back to the instantaneous score when fewer than
        /// <see cref="PlayerInfo.VarianceMinSamples"/> samples have been collected (e.g. a
        /// desync on the very first frames of the match).
        /// This covers the 2-player case (always 1-1) and any larger even split
        /// (2-2 in a 4-player lobby, etc.).
        /// </summary>
        private uint ResolveChecksumTie(
            MatchState match,
            ConcurrentDictionary<int, uint> frameMap,
            Dictionary<uint, int> freq,
            int maxCount)
        {
            double bestScore = double.MaxValue;
            uint bestChecksum = 0;
            bool found = false;

            foreach (var kvp in match.Players)
            {
                var player = kvp.Value;
                if (player.IsSpectator) continue;
                if (!frameMap.TryGetValue(player.PlayerIndex, out uint playerChecksum)) continue;

                // Only consider players whose checksum belongs to one of the tied groups.
                if (!freq.TryGetValue(playerChecksum, out int groupCount)) continue;
                if (groupCount != maxCount) continue;

                double score = ConnectionStabilityScore(player);

                if (!found || score < bestScore)
                {
                    bestScore = score;
                    bestChecksum = playerChecksum;
                    found = true;
                }
            }

            return bestChecksum;
        }

        /// <summary>
        /// Returns the player on the trusted (majority/winning) side who has the best
        /// connection quality, for use as the reference player in desync log messages.
        /// Returns <see langword="null"/> if no matching player is found.
        /// </summary>
        private PlayerInfo? FindBestQualityPlayerForChecksum(
            MatchState match,
            ConcurrentDictionary<int, uint> frameMap,
            uint targetChecksum)
        {
            double bestScore = double.MaxValue;
            PlayerInfo? best = null;

            foreach (var kvp in match.Players)
            {
                var player = kvp.Value;
                if (player.IsSpectator) continue;
                if (!frameMap.TryGetValue(player.PlayerIndex, out uint playerChecksum)) continue;
                if (playerChecksum != targetChecksum) continue;

                double score = ConnectionStabilityScore(player);
                if (best is null || score < bestScore)
                {
                    bestScore = score;
                    best = player;
                }
            }

            return best;
        }

        /// <summary>
        /// Returns a scalar score representing a player's connection and simulation
        /// stability over the lifetime of the match. Lower = more stable = more trusted.
        ///
        /// Primary metric (when enough samples exist): Welford population variance of
        /// <see cref="PlayerInfo.SmoothedPing"/> (ms²) plus Welford population variance
        /// of <see cref="PlayerInfo.SmoothRift"/> (frames²) scaled to ms² by multiplying
        /// by <c>TargetFrameTime²</c>. Both terms are then on the same ms² scale so
        /// neither dominates unfairly.
        ///
        /// Fallback (fewer than <see cref="PlayerInfo.VarianceMinSamples"/> samples):
        /// instantaneous score <c>SmoothedPing + |SmoothRift| × TargetFrameTime</c>,
        /// shifted to a high range so it never beats a player with real variance data.
        /// </summary>
        private double ConnectionStabilityScore(PlayerInfo player)
        {
            bool hasPingVariance = player.PingVarianceSampleCount >= PlayerInfo.VarianceMinSamples;
            bool hasRiftVariance = player.RiftVarianceSampleCount >= PlayerInfo.VarianceMinSamples;

            if (hasPingVariance && hasRiftVariance)
            {
                // Scale rift variance (frames²) → ms² so the two terms are comparable.
                double riftVarianceMs2 = player.RiftVariance * TargetFrameTime * TargetFrameTime;
                return player.PingVariance + riftVarianceMs2;
            }

            // Fallback: instantaneous quality, offset above any realistic variance score
            // so a player with match-long data always wins the tiebreak.
            const double FallbackOffset = 1_000_000.0;
            return FallbackOffset + player.SmoothedPing + MathF.Abs(player.SmoothRift) * TargetFrameTime;
        }

        // ═══════════════════════════════════════════
        //  History Pruning
        // ═══════════════════════════════════════════

        /// <summary>
        /// Removes input entries older than <c>LastVerifiedFrame - retentionFrames</c>
        /// from every player's input history dictionary.
        ///
        /// Called periodically from <see cref="RunTickLoop"/> via the
        /// <c>InputCleanupInterval</c> gate. Bounding input history prevents the
        /// per-player <see cref="ConcurrentDictionary{TKey,TValue}"/> from growing
        /// indefinitely over a long match (~29,000 entries at 60fps / 8 minutes).
        /// </summary>
        private void PruneInputHistory(MatchState match, uint retentionFrames)
        {
            uint verified = match.LastVerifiedFrame;
            if (verified < retentionFrames) return; // Not enough frames verified yet.

            uint cutoff = verified - retentionFrames;

            foreach (var inputMap in match.Inputs)
            {
                foreach (var key in inputMap.Keys)
                {
                    if (key < cutoff)
                        inputMap.TryRemove(key, out _);
                }
            }
        }

        /// <summary>
        /// Removes checksum entries older than <c>LastVerifiedFrame - retentionFrames</c>
        /// from <see cref="MatchState.FrameChecksums"/> and from each player's
        /// <see cref="PlayerInfo.Checksums"/> dictionary.
        ///
        /// Called periodically from <see cref="RunTickLoop"/> via the
        /// <c>ChecksumCleanupInterval</c> gate. Without pruning,
        /// <see cref="ProcessChecksums"/> must scan the entire history every time
        /// any client sends input, making it O(n frames) per packet.
        /// </summary>
        private void PruneChecksumHistory(MatchState match, uint retentionFrames)
        {
            // Use LastHandledChecksumFrame (>= LastVerifiedFrame) so that desynced frames,
            // which never advance LastVerifiedFrame, are still eligible for cleanup.
            uint handled = match.LastHandledChecksumFrame;
            if (handled < retentionFrames) return;

            uint cutoff = handled - retentionFrames;

            foreach (var key in match.FrameChecksums.Keys)
            {
                if (key < cutoff)
                    match.FrameChecksums.TryRemove(key, out _);
            }

            foreach (var kvp in match.Players)
            {
                var checksums = kvp.Value.Checksums;
                foreach (var key in checksums.Keys)
                {
                    if (key < cutoff)
                        checksums.TryRemove(key, out _);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void CalcRiftVariableTick(PlayerInfo player, uint serverFrame, ServerConfiguration config)
        {
            if (!player.HasNewPing || !player.HasNewFrame)
            {
                return;
            }

            float halfPingFrames = (player.SmoothedPing * 0.5f) / TargetFrameTime;
            float predictedClientFrame = player.LastClientFrame + halfPingFrames;

            // Raw rift: how far ahead client is RIGHT NOW
            float rawRift = predictedClientFrame - serverFrame;

            if (!player.RiftInit)
            {
                player.RiftInit = true;
                player.SmoothRift = rawRift - config.RiftCalculation.TargetRift;
                player.Rift = rawRift;
                player.HasNewPing = false;
                player.HasNewFrame = false;

                return;
            }

            // Calculate error early so we can decide whether to bypass the rate gate.
            // Positive error = client too far ahead, negative = client behind.
            float riftError = rawRift - config.RiftCalculation.TargetRift;

            // Apply the update interval gate only when the player is already near the
            // target. When divergence exceeds FastConvergenceThreshold (e.g. 1.5 frames),
            // correct every tick regardless of the interval — high-latency cross-region
            // players can drift faster than a coarse update interval can track.
            bool largeDeviation = MathF.Abs(riftError) >= config.RiftCalculation.FastConvergenceThreshold;
            bool gated = serverFrame % config.RiftCalculation.RiftUpdateInterval != 0
                         && serverFrame > config.RiftCalculation.RiftUpdateThreshold;

            if (gated && !largeDeviation)
            {
                return;
            }

            player.Rift = rawRift;

            if (config.RiftCalculation.UseAggressiveCorrection)
            {
                // Aggressive mode: snap quickly to reduce perceived delay
                if (MathF.Abs(riftError) < 0.2f)
                {
                    // Very close to target - hold steady
                    player.SmoothRift = riftError;
                }
                else if (MathF.Abs(riftError) < MathF.Abs(player.SmoothRift))
                {
                    // Converging - snap immediately
                    player.SmoothRift = riftError;
                }
                else
                {
                    // Diverging - use higher smoothing factor for faster response
                    float aggressiveAlpha = MathF.Min(RiftAlpha * 2.0f, 0.3f);
                    player.SmoothRift = aggressiveAlpha * riftError + (1f - aggressiveAlpha) * player.SmoothRift;
                }
            }
            else
            {
                // Conservative mode (original behavior)
                if (MathF.Abs(riftError) < 0.5f)
                {
                    player.SmoothRift *= 0.5f;
                    if (MathF.Abs(player.SmoothRift) < 0.01f)
                        player.SmoothRift = 0f;
                }
                else
                {
                    player.SmoothRift = RiftAlpha * riftError + (1f - RiftAlpha) * player.SmoothRift;
                }

                if (MathF.Abs(riftError) < MathF.Abs(player.SmoothRift))
                    player.SmoothRift = riftError;
            }

            player.SmoothRift = PlayerInfo.ClampFloat(player.SmoothRift, config.RiftCalculation.MaxRiftDeviation);
            player.Ping = (short)player.SmoothedPing;
            player.HasNewPing = false;
            player.HasNewFrame = false;

            // ── Welford online variance: ping ──
            // One sample per committed ping update (same cadence as SmoothedPing).
            // Written under player.Lock (held by the caller), so no extra synchronisation.
            {
                player.PingVarianceSampleCount++;
                double delta  = player.SmoothedPing - player.PingVarianceMean;
                player.PingVarianceMean += delta / player.PingVarianceSampleCount;
                double delta2 = player.SmoothedPing - player.PingVarianceMean;
                player.PingVarianceM2 += delta * delta2;
            }

            // ── Welford online variance: rift ──
            // Use the absolute rift error so the variance reflects deviation magnitude
            // regardless of direction (ahead vs. behind).
            {
                double sample = MathF.Abs(player.SmoothRift);
                player.RiftVarianceSampleCount++;
                double delta  = sample - player.RiftVarianceMean;
                player.RiftVarianceMean += delta / player.RiftVarianceSampleCount;
                double delta2 = sample - player.RiftVarianceMean;
                player.RiftVarianceM2 += delta * delta2;
            }

            // Metrics — read-only, after all state mutations
            ServerMetrics.RiftValue.Record(player.SmoothRift);
            ServerMetrics.RiftError.Record(riftError);
            ServerMetrics.PingValue.Record(player.SmoothedPing);

            // Track when we're making significant corrections
            if (MathF.Abs(riftError) > 1.0f)
            {
                ServerMetrics.RiftCorrections.Add(1);
            }

            if (player.SmoothRift > 1 || player.SmoothRift < -1 || player.SmoothedPing > 254)
            {
                Log.RiftInfo(_logger,
                    player.MatchId, player.PlayerIndex, player.PlayerName, player.Ping, player.SmoothRift,
                    player.Rift, predictedClientFrame, serverFrame);
            }
        }

        // ═══════════════════════════════════════════
        //  Tick Loop
        // ═══════════════════════════════════════════

        private void StartTickLoop(MatchState match)
        {
            if (!match.TryStartTick()) return;

            // LongRunning → dedicated OS thread, never starves ThreadPool
            Task.Factory.StartNew(
                () => RunTickLoop(match),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        private void RunTickLoop(MatchState match)
        {
            var config = ServerConfiguration.Instance;

            long targetIntervalTicks =
                (long)(match.TickIntervalMs / 1000.0 * Stopwatch.Frequency);
            long startTime = Stopwatch.GetTimestamp();
            long nextTickTime = startTime + targetIntervalTicks;
            long accumulatedError = 0;

            _logger.LogInformation("Stopwatch raw frequency resolution is: {Frequency}", Stopwatch.Frequency);

            // Get spin threshold from configuration
            long spinThreshold;
            if (config.Performance.UseAdaptiveSpinThreshold)
            {
                // Adaptive: reduce spin time when CPU is constrained
                spinThreshold = Environment.ProcessorCount <= 2
                    ? Stopwatch.Frequency / 2000   // 500μs for 2-core systems
                    : Stopwatch.Frequency / 500;    // 2ms for systems with spare cores
            }
            else
            {
                // Fixed threshold from config (microseconds → ticks)
                spinThreshold = (long)(config.Performance.SpinThresholdMicroseconds *
                    Stopwatch.Frequency / 1_000_000.0);
            }

            _logger.LogInformation(
                "Starting tick loop for match {MatchId} with target interval {Interval}ms, " +
                "spin threshold {SpinThreshold}μs (spinThreshold: {spinThreshold}), adaptive spin: {AdaptiveSpin}",
                match.MatchId, match.TickIntervalMs, spinThreshold * 1_000_000.0 / Stopwatch.Frequency, spinThreshold,
                config.Performance.UseAdaptiveSpinThreshold);

            int perfCount = 0;
            long perfStart = Stopwatch.GetTimestamp();

            while (match.IsTickRunning && _running)
            {
                // ── Tick (fully synchronous — zero async overhead) ──

                long tickStart = Stopwatch.GetTimestamp();
                Tick(match, config);

                // Sample histogram based on config
                if (match.CurrentFrame % config.Performance.MetricsSamplingInterval == 0)
                {
                    ServerMetrics.TickDurationUs.Record(
                        Stopwatch.GetElapsedTime(tickStart).TotalMicroseconds);
                }
                ServerMetrics.TicksProcessed.Add(1);

                // ── Check all-disconnected ──
                bool allDisconnected = true;
                foreach (var kvp in match.Players)
                {
                    if (!kvp.Value.Disconnected) { allDisconnected = false; break; }
                }

                if (allDisconnected && match.Players.Count > 0)
                {
                    _ = _httpHelper.SendEndMatchAsync(match.MatchId, match.Key);
                    match.StopTick();

                    _ = Events.SendAllPlayersDisconnectedEvent(this, StatusEventArgs.CreateNew(
                            description: "AllPlayersDisconnected",
                            matchEvent: "AllPlayersDisconnected",
                            matchDescription: $"All players disconnected in match {match.MatchId}. Ending match.",
                            matchKey: match.Key,
                            matchId: match.MatchId,
                            matchNumPlayers: match.Players.Count,
                            matchPlayerIds: match.Players.Select(p => p.Value.PlayerId).ToArray()
                            )
                        );

                    foreach (var kvp in match.Players)
                        _players.TryRemove(kvp.Key, out _);
                    match.Players.Clear();
                    foreach (var inputMap in match.Inputs) inputMap.Clear();
                    _matches.TryRemove(match.MatchId, out _);
                    ServerMetrics.MatchesEnded.Add(1);
                    Log.MatchCleanedUp(_logger, match.MatchId);
                    _ = Events.SendMatchEndEvent(this, StatusEventArgs.CreateNew(
                            description: "MatchEnded",
                            matchEvent: "MatchEnded",
                            matchDescription: $"Match {match.MatchId} ended and cleaned up after all players disconnected.",
                            matchKey: match.Key,
                            matchId: match.MatchId,
                            matchNumPlayers: match.Players.Count,
                            matchPlayerIds: match.Players.Select(p => p.Value.PlayerId).ToArray()
                            )
                        );

                    break;
                }

                // ── Wall-clock frame counter (UNCHANGED — identical to known-good) ──

                long now = Stopwatch.GetTimestamp();
                long elapsed = now - startTime;
                match.CurrentFrame = (uint)(elapsed / targetIntervalTicks);

                // ── Drift compensation (UNCHANGED) ──
                nextTickTime += targetIntervalTicks;
                if (accumulatedError != 0)
                {
                    long correction = accumulatedError / 4;
                    nextTickTime -= correction;
                    accumulatedError -= correction;
                }

                long waitTicks = nextTickTime - now;
                if (waitTicks < 0)
                {
                    accumulatedError += waitTicks;
                    nextTickTime = now;
                    long maxError = targetIntervalTicks * 3;

                    if (accumulatedError < -maxError)
                    {
                        accumulatedError = -maxError;
                    }

                    continue;
                }

                // ── Optimized hybrid sleep/yield/spin wait ──
                long remaining = nextTickTime - Stopwatch.GetTimestamp();
                if (remaining > spinThreshold)
                {
                    int sleepMs = (int)((remaining - spinThreshold) * 1000
                                         / Stopwatch.Frequency);
                    if (sleepMs > 0)
                        Thread.Sleep(sleepMs);
                }

                // NEW: Yield to other threads instead of pure spinning
                // This saves ~8% CPU while adding only ~30μs jitter
                while (Stopwatch.GetTimestamp() < nextTickTime)
                {
                    long remainingTicks = nextTickTime - Stopwatch.GetTimestamp();

                    // Only spin for final 50μs (was ~2000μs)
                    if (remainingTicks < Stopwatch.Frequency / 20000)  // 50μs
                        Thread.SpinWait(10);  // Reduced from 20 iterations
                    else
                        Thread.Yield();  // Let other threads/instances run
                }

                // ── Measure timing error ──
                long afterWait = Stopwatch.GetTimestamp();
                long timerError = (afterWait - now) - waitTicks;
                accumulatedError += timerError;

                // ── Perf reporting ──
                perfCount++;
                if (config.Logging.LogTickPerformance &&
                    perfCount >= config.Logging.TickPerformanceInterval)
                {
                    double avgUs = Stopwatch.GetElapsedTime(perfStart).TotalMicroseconds / perfCount;
                    Log.TickPerformance(_logger, avgUs);
                    perfCount = 0;
                    perfStart = Stopwatch.GetTimestamp();

                    _ = Events.SendTickPerformanceEvent(this, StatusEventArgs.CreateNew(
                            description: "TickPerformance",
                            matchEvent: "TickPerformance",
                            matchDescription: $"Average tick interval for matchID {match.MatchId}: {avgUs:F0} μs.",
                            matchKey: match.Key,
                            matchId: match.MatchId,
                            matchNumPlayers: match.Players.Count,
                            matchPlayerIds: match.Players.Select(p => p.Value.PlayerId).ToArray()
                            )
                        );
                }

                // ── Periodic history pruning ──
                // Both prune helpers are cheap no-ops when the watermark hasn't
                // advanced far enough; the modulo gate keeps them off the hot path.
                uint currentFrame = match.CurrentFrame;
                if (config.GameLogic.InputCleanupInterval > 0 &&
                    currentFrame % config.GameLogic.InputCleanupInterval == 0)
                {
                    PruneInputHistory(match, config.GameLogic.InputHistoryFrames);
                }

                if (config.DesyncDetection.EnableDesyncDetection &&
                    config.DesyncDetection.ChecksumCleanupInterval > 0 &&
                    currentFrame % config.DesyncDetection.ChecksumCleanupInterval == 0)
                {
                    PruneChecksumHistory(match, config.DesyncDetection.ChecksumRetentionFrames);
                }
            }
        }

        // ═══════════════════════════════════════════
        //  Tick Processing (sync, zero-alloc hot path)
        // ═══════════════════════════════════════════

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void Tick(MatchState match, ServerConfiguration config)
        {
            var ws = match.Workspace!;
            var gameConfig = config.GameLogic;

            // FIX #4: Add lock when snapshotting players (prevents race conditions)
            lock (match.Lock)
            {
                ws.RefreshPlayerSnapshot(match.Players);    // ← zero-alloc snapshot
            }
            uint serverFrame = match.CurrentFrame;

            // ── Rift + disconnect check ──
            for (int p = 0; p < ws.PlayerCount; p++)
            {
                var player = ws.PlayerSnapshot[p].Value;
                if (player.IsSpectator)
                {
                    continue; // Spectators don't have rift or disconnect logic
                }
                lock (player.Lock)
                {
                    CalcRiftVariableTick(player, serverFrame, config);

                    if (!player.Disconnected &&
                        Stopwatch.GetElapsedTime(player.LastInputTimestamp).TotalSeconds
                            > DisconnectTimeout && !player.IsSpectator)
                    {
                        player.Disconnected = true;
                        ServerMetrics.PlayersDisconnected.Add(1);
                        Log.PlayerTimedOut(_logger, player.PlayerIndex, player.MatchId, DisconnectTimeout);
                        _ = Events.SendPlayerDisconnectEvent(this, StatusEventArgs.CreateNew(
                                description: "PlayerTimeout",
                                matchEvent: "PlayerDisconnect",
                                matchDescription: $"Player {player.PlayerId} (name: {player.PlayerName}, character: {player.PlayerCharacter}) at PlayerIndex {player.PlayerIndex} timed out and was disconnected from match {player.MatchId} after {DisconnectTimeout} seconds without input.",
                                matchKey: match.Key,
                                matchId: match.MatchId,
                                matchNumPlayers: match.Players.Count,
                                matchPlayerId: player.PlayerId,
                                matchPlayerIds: [.. match.Players.Select(p => p.Value.PlayerId)]
                                )
                            );
                        continue;
                    }
                    if (player.Disconnected) continue;
                }
            }

            // ── Wait for minimum inputs (loop instead of LINQ .Any()) ──
            // Skip bot slots — bots don't send inputs, their input dict stays
            // empty, so iterating them would block the match forever.
            bool needMore = false;
            for (int i = 0; i < match.Inputs.Count; i++)
            {
                if (match.BotIndices.Contains(i)) continue;
                if (match.Inputs[i].Count < gameConfig.MinimumInputFrames) { needMore = true; break; }
            }
            if (needMore)
            {
                for (int p = 0; p < ws.PlayerCount; p++)
                    SendServerMessage(match, ws.PlayerSnapshot[p].Value,
                        ServerMessageType.StartGame, null);
                return;
            }

            // ── Build + send per-recipient ──
            // Pre-allocate sequence numbers to reduce lock contention
            uint sequenceBase;
            lock (match.Lock)
            {
                sequenceBase = match.SequenceCounter;
                match.SequenceCounter += (uint)ws.PlayerCount;
            }

            for (int r = 0; r < ws.PlayerCount; r++)
            {
                var recipient = ws.PlayerSnapshot[r].Value;
                if (recipient.Disconnected) continue;

                ws.ResetForRecipient();

                uint lastClientFrame;
                short ping;
                float smoothRift;
                lock (recipient.Lock)
                {
                    for (int i = 0; i < match.MaxPlayers && i < recipient.AckedFrames.Count; i++)
                        ws.AckedFrames[i] = recipient.AckedFrames[i];
                    lastClientFrame = recipient.LastClientFrame;
                    ping = recipient.Ping;
                    smoothRift = recipient.SmoothRift;  // ← Pure SmoothRift, no bias
                }

                ushort numPredictedOverrides = 0;

                for (int p = 0; p < ws.PlayerCount; p++)
                {
                    var peer = ws.PlayerSnapshot[p].Value;
                    if (peer.IsSpectator)
                    {
                        continue; // Spectators don't send inputs
                    }
                    int idx = peer.PlayerIndex;
                    var inputMap = match.Inputs[idx];

                    uint lastAck = ws.AckedFrames[idx];
                    uint nextFrame = lastAck + 1;
                    recipient.MissedInputs.TryGetValue((uint)idx, out uint missedCount);

                    if (inputMap.TryGetValue(nextFrame, out uint firstInput))
                    {
                        ws.Payload.StartFrame[idx] = nextFrame;
                        ws.Payload.InputPerFrame[idx].Add(firstInput);
                        ws.Payload.NumFrames[idx] = 1;
                        byte sentCount = 1;
                        uint f = nextFrame + 1;
                        while (sentCount < MaxInputsPerFrame
                            && inputMap.TryGetValue(f, out uint val))
                        {
                            ws.Payload.InputPerFrame[idx].Add(val);
                            ws.Payload.NumFrames[idx]++;
                            f++;
                            sentCount++;
                        }
                        recipient.MissedInputs[(uint)idx] = 0;
                    }
                    else if (missedCount < gameConfig.MissToleranceFrames)
                    {
                        ws.Payload.StartFrame[idx] = lastAck;
                        recipient.MissedInputs[(uint)idx] = missedCount + 1;
                        inputMap.TryGetValue(lastAck, out uint lastVal);
                        ws.Payload.InputPerFrame[idx].Add(lastVal);
                        ws.Payload.NumFrames[idx] = 1;
                        ServerMetrics.InputMisses.Add(1);
                    }
                    else
                    {
                        ws.Payload.StartFrame[idx] = nextFrame;
                        uint predictedCount = 0;
                        uint f = nextFrame;
                        inputMap.TryGetValue(lastAck, out uint lastKnownInput);

                        while (f < lastClientFrame && predictedCount < MaxInputsPerFrame)
                        {
                            uint framesMissed = f - lastAck;
                            // Prefer a real input for frame f if one has since arrived
                            // (can happen for frames beyond nextFrame in a burst-recovery packet).
                            // Fall back to a prediction and commit it via TryAdd so that
                            // AckedFrames can advance past this frame. Without committing the
                            // prediction, the client acks the ephemeral prediction and
                            // AckedFrames advances, but the server's nextFrame jumps past any
                            // real inputs that subsequently arrive for these frames — they are
                            // never forwarded and eventually pruned, causing a permanent desync.
                            // TryAdd is safe here: it will not overwrite a real input that
                            // arrived between the start of this tick and this point.
                            if (!inputMap.TryGetValue(f, out uint toSend))
                            {
                                toSend = InputPredictor.Predict(lastKnownInput, framesMissed);
                                inputMap.TryAdd(f, toSend);
                            }
                            ws.Payload.InputPerFrame[idx].Add(toSend);
                            predictedCount++;
                            f++;
                        }
                        ws.Payload.NumFrames[idx] = (byte)predictedCount;
                        numPredictedOverrides += (ushort)predictedCount;
                        ServerMetrics.InputPredictions.Add(predictedCount);
                    }
                }

                ws.Payload.NumPlayers = (byte)ws.PlayerCount;
                ws.Payload.NumPredictedOverrides = numPredictedOverrides;
                ws.Payload.Ping = ping;
                ws.Payload.Rift = smoothRift;
                ws.Payload.ChecksumAckFrame = match.LastVerifiedFrame;

                // ── Zero-alloc serialize → compress → send (with pre-allocated sequence) ──
                uint playerSequence = sequenceBase + (uint)r;
                SendPlayerInput(match, recipient, ws, playerSequence);
            }

            // ── Input cleanup every N frames (no LINQ, no sort) ──
            if (match.CurrentFrame % gameConfig.InputCleanupInterval == 0)
            {
                uint minKeep = match.CurrentFrame > gameConfig.InputHistoryFrames
                    ? match.CurrentFrame - gameConfig.InputHistoryFrames
                    : 0;

                for (int i = 0; i < match.Inputs.Count; i++)
                {
                    var histMap = match.Inputs[i];
                    if (histMap.Count <= gameConfig.InputHistoryFrames) continue;

                    // ConcurrentDictionary enumeration is lock-free, no array allocated
                    foreach (var kvp in histMap)
                    {
                        if (kvp.Key < minKeep)
                            histMap.TryRemove(kvp.Key, out _);
                    }
                }
            }

            // ── Checksum cleanup every N frames ──
            var desyncConfig = config.DesyncDetection;
            if (desyncConfig.EnableDesyncDetection &&
                match.CurrentFrame % desyncConfig.ChecksumCleanupInterval == 0)
            {
                uint minKeepChecksum = match.CurrentFrame > desyncConfig.ChecksumRetentionFrames
                    ? match.CurrentFrame - desyncConfig.ChecksumRetentionFrames
                    : 0;

                foreach (var kvp in match.FrameChecksums)
                {
                    if (kvp.Key < minKeepChecksum)
                        match.FrameChecksums.TryRemove(kvp.Key, out _);
                }

                // Also prune each player's per-player lookup.
                for (int p = 0; p < ws.PlayerCount; p++)
                {
                    var player = ws.PlayerSnapshot[p].Value;
                    foreach (var kvp in player.Checksums)
                    {
                        if (kvp.Key < minKeepChecksum)
                            player.Checksums.TryRemove(kvp.Key, out _);
                    }
                }
            }
        }

        // ═══════════════════════════════════════════
        //  Sending
        // ═══════════════════════════════════════════

        /// <summary>
        /// Hot-path send: serialize into workspace buffer → compress into workspace
        /// buffer → synchronous SendTo. ZERO heap allocation for data buffers.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void SendPlayerInput(MatchState match, PlayerInfo player, TickWorkspace ws, uint sequence)
        {
            if (player.Disconnected) return;

            var header = new ServerHeader {
                Type = ServerMessageType.PlayerInput,
                Sequence = sequence  // Use pre-allocated sequence (no lock needed)
            };

            // ── Serialize directly into workspace buffer (SpanWriter, zero-alloc) ──
            int serializedLen = MessageSerializer.SerializePlayerInputTo(
                header, ws.Payload, match.MaxPlayers, ws.SerializeBuffer);

            // ── Compress into workspace buffer (output byte[] is pre-allocated) ──
            int compressedLen = CompressionHelper.CompressTo(
                ws.SerializeBuffer.AsSpan(0, serializedLen), ws.CompressBuffer);

            // ── Synchronous UDP send (non-blocking for small datagrams) ──
            long ts = Stopwatch.GetTimestamp();
            lock (_sendLock)
            {
                try
                {
                    _socket.SendTo(ws.CompressBuffer, 0, compressedLen,
                        SocketFlags.None, player.EndPoint);
                    ServerMetrics.PacketsSent.Add(1);
                }
                catch (SocketException ex)
                {
                    Log.SendFailed(_logger, player.PlayerIndex, player.MatchId, ex);
                    _ = Events.SendErrorEvent(this, StatusEventArgs.CreateNew(
                            description: "SendFailed",
                            matchEvent: "ErrorInputSendFailed",
                            matchDescription: $"Failed to send input message to Player {player.PlayerId} (name: {player.PlayerName}, character: {player.PlayerCharacter}) at PlayerIndex {player.PlayerIndex} in match {player.MatchId}: {ex.Message}",
                            matchKey: match.Key,
                            matchPlayerId: player.PlayerId,
                            exception: ex
                            )
                        );
                    player.Disconnected = true;
                    _ = Events.SendPlayerDisconnectEvent(this, StatusEventArgs.CreateNew(
                            description: "PlayerDisconnect",
                            matchEvent: "ErrorPlayerDisconnect",
                            matchDescription: $"Player {player.PlayerId} (name: {player.PlayerName}, character: {player.PlayerCharacter}) at PlayerIndex {player.PlayerIndex} was disconnected from match {player.MatchId} due to send failure: {ex.Message}",
                            matchKey: match.Key,
                            matchId: match.MatchId,
                            matchNumPlayers: match.Players.Count,
                            matchPlayerId: player.PlayerId,
                            matchPlayerIds: [.. match.Players.Select(p => p.Value.PlayerId)],
                            exception: ex
                            )
                        );
                    return;
                }
            }

            player.LastSentTimestamp = ts;
            player.PendingPings[sequence] = ts;

        }

        /// <summary>
        /// Cold-path send: used for non-tick messages (connection replies, ping requests,
        /// start game, player config). Allocates normally — called infrequently.
        /// </summary>
        private uint SendServerMessage(
            MatchState match, PlayerInfo player, ServerMessageType type, object? payload)
        {
            var config = ServerConfiguration.Instance;

            if (player.Disconnected) return 0;

            var header = new ServerHeader { Type = type };
            lock (match.Lock)
            {
                header.Sequence = ++match.SequenceCounter;
            }

            var buf = MessageSerializer.SerializeServerMessage(header, payload, match.MaxPlayers);
            var compressed = CompressionHelper.Compress(buf);

            // ── Diagnostic: log outbound message details ──
            //_logger.LogDebug(
            //    "OUT → P{Index} type={Type} seq={Seq} raw={RawLen}b compressed={CompLen}b " +
            //    "first4=[{Hex}]",
            //    player.PlayerIndex, type, header.Sequence,
            //    buf.Length, compressed.Length,
            //    Convert.ToHexString(compressed, 0, Math.Min(compressed.Length, 4)));

            lock (_sendLock)
            {
                try
                {
                    _socket.SendTo(compressed, SocketFlags.None, player.EndPoint);
                    ServerMetrics.PacketsSent.Add(1);
                }
                catch (SocketException ex)
                {
                    _ = Events.SendErrorEvent(this, StatusEventArgs.CreateNew(
                            description: "SendFailed",
                            matchEvent: "ErrorServerSendFailed",
                            matchDescription: $"Failed to send server message to Player {player.PlayerId} (name: {player.PlayerName}, character: {player.PlayerCharacter}) at PlayerIndex {player.PlayerIndex} in match {player.MatchId}: {ex.Message}",
                            matchKey: match.Key,
                            matchPlayerId: player.PlayerId,
                            exception: ex
                            )
                        );
                    _logger.LogError("Send failed to player {Index}: {Err}",
                        player.PlayerIndex, ex.Message);
                    player.Disconnected = true;
                    _ = Events.SendPlayerDisconnectEvent(this, StatusEventArgs.CreateNew(
                            description: "PlayerDisconnect",
                            matchEvent: "ErrorPlayerDisconnect",
                            matchDescription: $"Player {player.PlayerId} (name: {player.PlayerName}, character: {player.PlayerCharacter}) at PlayerIndex {player.PlayerIndex} was disconnected from match {player.MatchId} due to send failure: {ex.Message}",
                            matchKey: match.Key,
                            matchId: match.MatchId,
                            matchNumPlayers: match.Players.Count,
                            matchPlayerId: player.PlayerId,
                            matchPlayerIds: [.. match.Players.Select(p => p.Value.PlayerId)],
                            exception: ex
                            )
                        );
                    return 0;
                }
            }

            return header.Sequence;
        }
    }
}
