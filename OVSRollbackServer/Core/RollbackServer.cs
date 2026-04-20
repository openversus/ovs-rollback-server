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
        private OVSMatchConfig? matchConfig = default;
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
            _httpHelper = new HTTPHelper(_logger);
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
                    var data = buffer[..result.ReceivedBytes].ToArray();
                    var remote = (IPEndPoint)result.RemoteEndPoint;

                    if (!connections.Contains(remote.Address.ToString()))
                    {
                        connections.Add(remote.Address.ToString());
                        Log.ConnectionReceived(_logger, remote.Address.ToString());
                    }

                    ServerMetrics.PacketsReceived.Add(1);

                    // NEW: Handle synchronously - we're already on ThreadPool, no need for Task
                    HandleMessage(data, result.ReceivedBytes, remote);
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

        private void HandleMessage(byte[] buffer, int length, IPEndPoint remote)
        {
            var config = ServerConfiguration.Instance;


            try
            {
                // ── Hex dump of first 16 raw bytes for diagnosis ──
                // string rawHex = Convert.ToHexString(buffer, 0, Math.Min(length, 16));
                //        _logger.LogDebug("Received {Len} bytes from {Remote} raw:[{Hex}]",
                //            length, remote, rawHex);

                // ── Decompress with C++ catch-all pattern ──
                //
                //  C++ equivalent:
                //    try { decompressedData = decompressData(receivedData); }
                //    catch (...) { decompressedData = receivedData; }
                //

                byte[] decompressed;
                try
                {
                    // Pass as ReadOnlySpan<byte> — avoids allocating a new byte[] slice
                    //    decompressed = CompressionHelper.Decompress(new ReadOnlySpan<byte>(buffer, 0, length));
                    decompressed = CompressionHelper.Decompress(buffer[..length]);
                }
                catch (Exception dex)
                {
                    // Matches C++: catch(...) { decompressedData = receivedData; }
                    _logger.LogWarning(dex,
                        "Decompress failed for {Len} bytes from {Remote}, using raw data",
                        length, remote);
                    decompressed = buffer[..length];
                }

                // ── Hex dump of first 16 decompressed bytes ──
                // string decHex = Convert.ToHexString(decompressed, 0, Math.Min(decompressed.Length, 16));
                //        _logger.LogDebug(
                //           "Decompressed {InLen}->{OutLen} bytes fallback={Fallback} dec:[{Hex}]",
                //           length, decompressed.Length, usedRawFallback, decHex);

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

                //        _logger.LogDebug("Parsed {Type} seq={Seq} from {Remote}",
                //           clientMsg.Value.Header.Type, clientMsg.Value.Header.Sequence, remote);

                var header = clientMsg.Value.Header;
                var type = header.Type;

                MatchState? match = null;
                PlayerInfo? player = null;

                if (type == ClientMessageType.NewConnection)
                {
                    var payload = (NewConnectionPayload)clientMsg.Value.Payload;
                    // NEW: Synchronous - HTTP fetch blocks but we're on ThreadPool already
                    player = HandleNewConnection(payload, remote);
                    if (player != null)
                        _matches.TryGetValue(player.MatchId, out match);
                }
                else
                {
                    string key = $"{remote.Address}:{remote.Port}";
                    if (_players.TryGetValue(key, out player) && player != null)
                        _matches.TryGetValue(player.MatchId, out match);
                }

                if (player is null || match is null)
                {
                    return;
                }

                if (header.Sequence <= player.LastSeqRecv)
                {
                    return;
                }
                player.LastSeqRecv = header.Sequence;

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

        private PlayerInfo? HandleNewConnection(
            NewConnectionPayload payload, IPEndPoint remote)
        {
            string key = $"{remote.Address}:{remote.Port}";
            var matchData = payload.MatchData;

            MatchState? match;
            _matchCreationLock.Wait();  // Synchronous wait (was async)
            OVSMatchConfig? config = null;

            try
            {
                if (!_matches.TryGetValue(matchData.MatchId, out match))
                {
                    Log.NewMatch(_logger, matchData.MatchId);

                    // Synchronous HTTP call - we're on ThreadPool, blocking is OK
                    config = _httpHelper.FetchMatchConfigAsync(matchData.MatchId, matchData.Key)
                        .GetAwaiter().GetResult();
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
                        Inputs = new(config.ActualPlayers),
                        Workspace = new TickWorkspace(config.MaxPlayers)
                        //Workspace = new TickWorkspace(config.ActualPlayers)
                    };
                    for (int i = 0; i < config.ActualPlayers; i++)
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

                    matchConfig = config; // Store in server-level cache for quick access during player joins
                }
            }
            finally { _matchCreationLock.Release(); }

            if (_players.TryGetValue(key, out var existing))
            {
                return existing;
            }

            string playerID = "Unknown";
            string playerName = "Unknown";
            string playerCharacter = "Unknown";
            ushort payloadIndex = payload.PlayerData.PlayerIndex;

            if (match.Players.TryGetValue(key, out var existingPlayer))
            {
                return match.Players[key];
            }

            else
            {
                if (null != matchConfig && matchConfig != default)
                {
                    foreach (OvsPlayer? player in matchConfig.Players)
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
                IsSpectator = payload.PlayerData.PlayerIndex == 8888 ? true : false,
                LastSeqRecv = 0,
                LastSeqSent = 0,
                AckedFrames = new List<uint>(new uint[match.MaxPlayers]),
                Ping = 0,
                Ready = payload.PlayerData.PlayerIndex == 8888 ? true : false,
                LastClientFrame = 0,
                LastInputTimestamp = Stopwatch.GetTimestamp(),
                Rift = 0
            };

            match.Players[key] = newPlayer;
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

            if (match.ActualPlayers == match.MaxPlayers - match.NumSpectators)
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
                    //configValues.Add(mapping[i]);
                    configValues.Add(mapping[i % (configValues.Capacity + 1)]);
                }
                //configValues.Add(mapping[i % 4]);

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

        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void CalcRiftVariableTick(PlayerInfo player, uint serverFrame)
        {
            var config = ServerConfiguration.Instance;


            if (serverFrame % config.RiftCalculation.RiftUpdateInterval != 0 && serverFrame > config.RiftCalculation.RiftUpdateThreshold)
            {

                return;
            }
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
                // NEW: Initialize with bias toward target rift
                player.SmoothRift = rawRift - config.RiftCalculation.TargetRift;
                player.Rift = rawRift;
                player.HasNewPing = false;
                player.HasNewFrame = false;

                return;
            }

            player.Rift = rawRift;

            //bool noGCActive = false;
            //try
            //{
            //    // Try to enter NoGCRegion for this single tick
            //    noGCActive = GC.TryStartNoGCRegion(
            //        config.Performance.GarbageCollectionFreeRAMThreshold,
            //        disallowFullBlockingGC: true);
            //}
            //catch (InvalidOperationException)
            //{
            //    // Already in NoGCRegion from previous iteration - this is fine
            //    noGCActive = false;
            //}
            //catch (ArgumentOutOfRangeException)
            //{
            //    // User provided invalid threshold, but don't de because of it - log once and continue without NoGCRegion
            //    _logger.LogWarning(
            //        "Invalid NoGCRegion threshold configured: {Threshold} bytes. " +
            //        "NoGCRegion will be disabled. Please appsettings.json and ensure " +
            //        "the threshold is less than the total available memory on the server, and " +
            //        "that the value provided is a positive integer measured in Megabytes (e.g. 512 for ~512MB).",
            //        config.Performance.GarbageCollectionFreeRAMThreshold);
            //    noGCActive = false;
            //}

            // NEW: Calculate error from TARGET rift (not zero)
            // Positive error = client too far ahead, negative = client behind
            float riftError = rawRift - config.RiftCalculation.TargetRift;

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
                    player.MatchId, player.PlayerIndex, player.Ping, player.SmoothRift,
                    player.Rift, predictedClientFrame, serverFrame);
            }

            //if (noGCActive && GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
            //{
            //    try
            //    {
            //        GC.EndNoGCRegion();
            //    }
            //    catch
            //    {
            //    }
            //}

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
                "Starting tick loop for match {MatchId} with target interval {Interval} ms, " +
                "spin threshold {SpinThreshold} μs (spinThreshold: {spinThreshold}), adaptive spin: {AdaptiveSpin}",
                match.MatchId, match.TickIntervalMs, spinThreshold * 1_000_000.0 / Stopwatch.Frequency, spinThreshold,
                config.Performance.UseAdaptiveSpinThreshold);

            int perfCount = 0;
            long perfStart = Stopwatch.GetTimestamp();

            while (match.IsTickRunning && _running)
            {
                // ── Tick (fully synchronous — zero async overhead) ──

                long tickStart = Stopwatch.GetTimestamp();
                Tick(match);

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
            }
        }

        // ═══════════════════════════════════════════
        //  Tick Processing (sync, zero-alloc hot path)
        // ═══════════════════════════════════════════

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void Tick(MatchState match)
        {
            var ws = match.Workspace!;
            var gameConfig = ServerConfiguration.Instance.GameLogic;

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
                    CalcRiftVariableTick(player, serverFrame);

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
            bool needMore = false;
            for (int i = 0; i < match.Inputs.Count; i++)
            {
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
                    else if (missedCount < 10)
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
                            uint predicted = InputPredictor.Predict(lastKnownInput, framesMissed);
                            inputMap[f] = predicted;
                            ws.Payload.InputPerFrame[idx].Add(predicted);
                            predictedCount++;
                            f++;
                        }
                        ws.Payload.NumFrames[idx] = (byte)predictedCount;
                        numPredictedOverrides = (ushort)predictedCount;
                        ServerMetrics.InputPredictions.Add(predictedCount);
                    }
                }

                ws.Payload.NumPlayers = (byte)ws.PlayerCount;
                ws.Payload.NumPredictedOverrides = numPredictedOverrides;
                ws.Payload.Ping = ping;
                ws.Payload.Rift = smoothRift;

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
            var config = ServerConfiguration.Instance;

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
