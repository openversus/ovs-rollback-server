// RollbackServer.cs
using Microsoft.Extensions.Logging;
using OVS.Rollback.Models;
using OVS.Rollback.Utils;
using OVS.Rollback.Core;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace OVS.Rollback.Core
{
    public sealed partial class RollbackServer : IAsyncDisposable
    {
        private const float TargetFrameTime = 1000f / 60f;
        private const float PingAlpha = 0.1f;
        private const float RiftAlpha = 0.05f;
        private const byte MaxInputsPerFrame = 30;
        private const int DisconnectTimeout = 30;

        private readonly ushort _port;
        private readonly int _maxPlayers;
        private readonly Socket _socket;
        private readonly object _sendLock = new();          // ← NEW: protects concurrent SendTo
        private readonly HttpClient _httpClient = new();
        private readonly ILogger<RollbackServer> _logger;

        private readonly ConcurrentDictionary<string, MatchState> _matches = new();
        private readonly ConcurrentDictionary<string, PlayerInfo> _players = new();
        private readonly SemaphoreSlim _matchCreationLock = new(1, 1);

        // ── Lifecycle ──
        private volatile bool _running;
        private Task? _udpTask;

        // ── Configuration ──
        public string BaseUrl { get; private set; } = "";
        public bool IsOVS { get; private set; }
        public bool IsMVSI { get; private set; }

        private static class Endpoints
        {
            public const string OVSRegister = "/ovs_register";
            public const string OVSEndMatch = "/ovs_end_match";
            public const string MVSIRegister = "/mvsi_register";
            public const string MVSIEndMatch = "/mvsi_end_match";
        }

        // ═══════════════════════════════════════════
        //  Constructor / Lifecycle
        // ═══════════════════════════════════════════

        public RollbackServer(
            ILogger<RollbackServer> logger,
            ushort port = Constants.GameServerPort,
            int maxPlayers = Constants.MaxPlayers)
        {
            _logger = logger;
            _port = port;
            _maxPlayers = maxPlayers;
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            BaseUrl = GetBaseUrlFromEnv();
            if (string.IsNullOrEmpty(BaseUrl))
                throw new InvalidOperationException(
                    "No base URL configured. Please set the OVS_SERVER environment variable.");

            Log.Listening(_logger, _port);
        }

        public void Start()
        {
            if (_running) return;
            _running = true;

            _socket.Bind(new IPEndPoint(IPAddress.Any, _port));

            // ← NEW: Apply low-latency socket options (DSCP EF, buffers, DontFragment)
            SocketConfigurator.ConfigureForLowLatency(_socket, _logger);

            _udpTask = Task.Run(RunUdpServerAsync);

            string serverType = IsOVS ? "OVS" : (IsMVSI ? "MVSI" : "Unknown");
            Log.ServerStarted(_logger, serverType, _port);
            Log.MatchEndpoint(_logger, BaseUrl);
        }

        public async Task StopAsync()
        {
            if (!_running) return;
            _running = false;

            try { _socket.Shutdown(SocketShutdown.Both); } catch { }
            _socket.Close();

            if (_udpTask is not null)
            {
                try { await _udpTask; }
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
            var buffer = new byte[1024];
            var anyEp = new IPEndPoint(IPAddress.Any, 0);

            while (_running)
            {
                try
                {
                    var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, anyEp);
                    var data = buffer[..result.ReceivedBytes].ToArray();
                    var remote = (IPEndPoint)result.RemoteEndPoint;

                    ServerMetrics.PacketsReceived.Add(1);                    // ← NEW metric
                    _ = HandleMessageAsync(data, result.ReceivedBytes, remote);
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

        private async Task HandleMessageAsync(byte[] buffer, int length, IPEndPoint remote)
        {
            try
            {
                // ── Hex dump of first 16 raw bytes for diagnosis ──
                string rawHex = Convert.ToHexString(buffer, 0, Math.Min(length, 16));
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
                string decHex = Convert.ToHexString(decompressed, 0, Math.Min(decompressed.Length, 16));
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
                    // HTTP fetch is truly async — block here since we're on a ThreadPool thread
                    player = HandleNewConnectionAsync(payload, remote).GetAwaiter().GetResult();
                    if (player != null)
                        _matches.TryGetValue(player.MatchId, out match);
                }
                else
                {
                    string key = $"{remote.Address}:{remote.Port}";
                    if (_players.TryGetValue(key, out player) && player != null)
                        _matches.TryGetValue(player.MatchId, out match);
                }

                if (player is null || match is null) return;

                if (header.Sequence <= player.LastSeqRecv) return;
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
                        break;
                    case ClientMessageType.Input:
                        HandleClientInput(match, player, (InputPayload)clientMsg.Value.Payload);
                        break;
                    case ClientMessageType.Disconnecting:
                        player.Disconnected = true;
                        ServerMetrics.PlayersDisconnected.Add(1);
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

        private async Task<PlayerInfo?> HandleNewConnectionAsync(
            NewConnectionPayload payload, IPEndPoint remote, bool debug = false)
        {
            string key = $"{remote.Address}:{remote.Port}";
            var matchData = payload.MatchData;

            MatchState? match;
            await _matchCreationLock.WaitAsync();
            try
            {
                if (!_matches.TryGetValue(matchData.MatchId, out match))
                {
                    Log.NewMatch(_logger, matchData.MatchId);
                    var config = await FetchMatchConfigAsync(matchData.MatchId, matchData.Key);
                    if (config is null)
                    {
                        Log.FetchConfigFailed(_logger, matchData.MatchId, new InvalidDataException("Match config is null."));
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
                        //Inputs = Enumerable.Range(0, config.MaxPlayers)
                        //    .Select(_ =>
                        //    new ConcurrentDictionary<uint, uint>())
                        //    .ToList(),
                        Inputs = new(config.MaxPlayers),
                        Workspace = new TickWorkspace(config.MaxPlayers)
                    };
                    for (int i = 0; i < config.MaxPlayers; i++)
                        match.Inputs.Add(new ConcurrentDictionary<uint, uint>());
                    _matches[matchData.MatchId] = match;
                    ServerMetrics.MatchesStarted.Add(1);
                }
            }
            finally { _matchCreationLock.Release(); }

            if (_players.TryGetValue(key, out var existing))
                return existing;

            var newPlayer = new PlayerInfo {
                EndPoint = remote,
                MatchId = matchData.MatchId,
                PlayerIndex = payload.PlayerData.PlayerIndex,
                LastSeqRecv = 0,
                LastSeqSent = 0,
                AckedFrames = new List<uint>(new uint[match.MaxPlayers]),
                Ping = 0,
                //Ready = debug,
                Ready = false,
                LastClientFrame = 0,
                LastInputTimestamp = Stopwatch.GetTimestamp(),
                Rift = 0,
                Emulated = debug
            };

            match.Players[key] = newPlayer;
            _players[key] = newPlayer;
            ServerMetrics.PlayersConnected.Add(1);
            Log.PlayerJoined(_logger, payload.PlayerData.PlayerIndex, matchData.MatchId);

            var reply = new NewConnectionReplyPayload {
                Success = 0,
                MatchNumPlayers = (byte)match.Players.Count,
                PlayerIndex = (byte)newPlayer.PlayerIndex,
                MatchDurationInFrames = match.DurationInFrames,
                IsValidationServerDebugMode = 0
            };
            SendServerMessage(match, newPlayer, ServerMessageType.NewConnectionReply, reply);

            if (match.Players.Count == match.MaxPlayers)
                StartPingPhase(match);

            return newPlayer;
        }

        // ═══════════════════════════════════════════
        //  Ping Phase (unchanged logic)
        // ═══════════════════════════════════════════

        private void StartPingPhase(MatchState match)
        {
            Log.PingPhaseStarted(_logger, match.MatchId);

            _ = Task.Run(async () => {
                try
                {
                    BroadcastRequestQuality(match);
                    match.PingPhaseCount++;

                    for (uint i = 1; i < match.PingPhaseTotal && _running; i++)
                    {
                        await Task.Delay(50);
                        BroadcastRequestQuality(match);
                        match.PingPhaseCount++;
                    }

                    BroadcastPlayersConfiguration(match);
                }
                catch (Exception ex)
                {
                    Log.PingPhaseError(_logger, match.MatchId, ex);
                }
            });
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
            ReadOnlySpan<ushort> mapping = stackalloc ushort[] { 0, 256, 513, 769 };
            int count = 0;
            foreach (var _ in match.Players) count++;

            foreach (var kvp in match.Players)
            {
                var player = kvp.Value;
                if (player.Disconnected) continue;

                var configValues = new List<ushort>(match.MaxPlayers);
                for (int i = 0; i < match.MaxPlayers; i++)
                    configValues.Add(mapping[i % 4]);

                var payload = new PlayersConfigurationDataPayload {
                    NumPlayers = (byte)count,
                    ConfigValues = configValues
                };
                SendServerMessage(match, player, ServerMessageType.PlayersConfigurationData, payload);
            }
        }

        // ═══════════════════════════════════════════
        //  Input & Acknowledgement Handlers (unchanged logic)
        // ═══════════════════════════════════════════

        private void HandlePlayerInputAck(MatchState match, PlayerInfo player, PlayerInputAckPayload payload)
        {
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

            // ← CHANGED: loop instead of .All() LINQ
            bool allReady = true;
            foreach (var kvp in match.Players)
            {
                if (!kvp.Value.Ready) { allReady = false; break; }
            }

            if (allReady)
            {
                foreach (var kvp in match.Players)
                    SendServerMessage(match, kvp.Value, ServerMessageType.StartGame, null);

                if (!match.IsTickRunning)
                    StartTickLoop(match);
            }
        }


        private void HandleClientInput(MatchState match, PlayerInfo player, InputPayload payload)
        {
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
            if (serverFrame % 60 != 0 && serverFrame > 500) return;
            if (!player.HasNewPing || !player.HasNewFrame) return;

            float halfPingFrames = (player.SmoothedPing * 0.5f) / TargetFrameTime;
            float predictedClientFrame = player.LastClientFrame + halfPingFrames;

            if (!player.RiftInit)
            {
                player.RiftInit = true;
                player.SmoothRift = predictedClientFrame - serverFrame;
            }
            else
            {
                float rawRift = predictedClientFrame - serverFrame;
                player.Rift = rawRift;

                if (MathF.Abs(rawRift) < 1f)
                {
                    player.SmoothRift *= 0.5f;
                    if (MathF.Abs(player.SmoothRift) < 0.01f)
                        player.SmoothRift = 0f;
                }
                else
                {
                    player.SmoothRift =
                        RiftAlpha * rawRift + (1f - RiftAlpha) * player.SmoothRift;
                }

                if (MathF.Abs(rawRift) < MathF.Abs(player.SmoothRift))
                    player.SmoothRift = rawRift;
            }

            player.SmoothRift = PlayerInfo.ClampFloat(player.SmoothRift, 20f);
            player.Ping = (short)player.SmoothedPing;
            player.HasNewPing = false;
            player.HasNewFrame = false;

            // Metrics — read-only, after all state mutations
            ServerMetrics.RiftValue.Record(player.SmoothRift);
            ServerMetrics.PingValue.Record(player.SmoothedPing);

            if (player.SmoothRift > 1 || player.SmoothRift < -1 || player.SmoothedPing > 254)
            {
                Log.RiftInfo(_logger,
                    player.MatchId, player.PlayerIndex, player.Ping, player.SmoothRift,
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
            long targetIntervalTicks =
                (long)(match.TickIntervalMs / 1000.0 * Stopwatch.Frequency);
            long startTime = Stopwatch.GetTimestamp();
            long nextTickTime = startTime + targetIntervalTicks;
            long accumulatedError = 0;

            // Busy-spin for the last ~2ms of each tick for sub-ms precision
            long spinThreshold = Stopwatch.Frequency / 500;

            int perfCount = 0;
            long perfStart = Stopwatch.GetTimestamp();

            while (match.IsTickRunning && _running)
            {
                // ── Tick (fully synchronous — zero async overhead) ──
                long tickStart = Stopwatch.GetTimestamp();
                Tick(match);
                ServerMetrics.TickDurationUs.Record(
                    Stopwatch.GetElapsedTime(tickStart).TotalMicroseconds);
                ServerMetrics.TicksProcessed.Add(1);

                // ── Check all-disconnected ──
                bool allDisconnected = true;
                foreach (var kvp in match.Players)
                {
                    if (!kvp.Value.Disconnected) { allDisconnected = false; break; }
                }

                if (allDisconnected && match.Players.Count > 0)
                {
                    _ = SendEndMatchAsync(match.MatchId, match.Key);
                    match.StopTick();

                    foreach (var kvp in match.Players)
                        _players.TryRemove(kvp.Key, out _);
                    match.Players.Clear();
                    foreach (var inputMap in match.Inputs) inputMap.Clear();
                    _matches.TryRemove(match.MatchId, out _);
                    ServerMetrics.MatchesEnded.Add(1);
                    Log.MatchCleanedUp(_logger, match.MatchId);
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
                    if (accumulatedError < -maxError) accumulatedError = -maxError;
                    continue;
                }

                // ── Hybrid sleep/spin wait ──
                long remaining = nextTickTime - Stopwatch.GetTimestamp();
                if (remaining > spinThreshold)
                {
                    int sleepMs = (int)((remaining - spinThreshold) * 1000
                                         / Stopwatch.Frequency);
                    if (sleepMs > 0)
                        Thread.Sleep(sleepMs);
                }
                while (Stopwatch.GetTimestamp() < nextTickTime)
                    Thread.SpinWait(20);

                // ── Measure timing error ──
                long afterWait = Stopwatch.GetTimestamp();
                long timerError = (afterWait - now) - waitTicks;
                accumulatedError += timerError;

                // ── Perf reporting ──
                perfCount++;
                if (perfCount >= 500)
                {
                    double avgUs = Stopwatch.GetElapsedTime(perfStart).TotalMicroseconds / perfCount;
                    Log.TickPerformance(_logger, avgUs);
                    perfCount = 0;
                    perfStart = Stopwatch.GetTimestamp();
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
            ws.RefreshPlayerSnapshot(match.Players);    // ← zero-alloc snapshot
            uint serverFrame = match.CurrentFrame;

            // ── Rift + disconnect check ──
            for (int p = 0; p < ws.PlayerCount; p++)
            {
                var player = ws.PlayerSnapshot[p].Value;
                lock (player.Lock)
                {
                    CalcRiftVariableTick(player, serverFrame);

                    if (!player.Disconnected &&
                        Stopwatch.GetElapsedTime(player.LastInputTimestamp).TotalSeconds
                            > DisconnectTimeout)
                    {
                        player.Disconnected = true;
                        ServerMetrics.PlayersDisconnected.Add(1);
                        Log.PlayerTimedOut(_logger, player.PlayerIndex, player.MatchId, DisconnectTimeout);
                        continue;
                    }
                    if (player.Disconnected) continue;
                }
            }

            // ── Wait for minimum inputs (loop instead of LINQ .Any()) ──
            bool needMore = false;
            for (int i = 0; i < match.Inputs.Count; i++)
            {
                if (match.Inputs[i].Count < 10) { needMore = true; break; }
            }
            if (needMore)
            {
                for (int p = 0; p < ws.PlayerCount; p++)
                    SendServerMessage(match, ws.PlayerSnapshot[p].Value,
                        ServerMessageType.StartGame, null);
                return;
            }

            // ── Build + send per-recipient ──
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

                // ── Zero-alloc serialize → compress → send ──
                SendPlayerInput(match, recipient, ws);
            }

            // ── Input cleanup every 200 frames (no LINQ, no sort) ──
            if (match.CurrentFrame % 200 == 0)
            {
                uint minKeep = match.CurrentFrame > 150 ? match.CurrentFrame - 150 : 0;
                for (int i = 0; i < match.Inputs.Count; i++)
                {
                    var histMap = match.Inputs[i];
                    if (histMap.Count <= 150) continue;

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
        private void SendPlayerInput(MatchState match, PlayerInfo player, TickWorkspace ws)
        {
            if (player.Disconnected) return;

            var header = new ServerHeader { Type = ServerMessageType.PlayerInput };
            lock (match.Lock)
            {
                header.Sequence = ++match.SequenceCounter;
            }

            /*
                        // ── Serialize directly into workspace buffer (SpanWriter, zero-alloc) ──
                        int serializedLen = MessageSerializer.SerializePlayerInputTo(
                            header, ws.Payload, match.MaxPlayers, ws.SerializeBuffer);

                        // ── Compress into workspace buffer (output byte[] is pre-allocated) ──
                        int compressedLen = CompressionHelper.CompressTo(
                            ws.SerializeBuffer.AsSpan(0, serializedLen), ws.CompressBuffer);
            */
            // ── Synchronous UDP send (non-blocking for small datagrams) ──
            long ts = Stopwatch.GetTimestamp();
            lock (_sendLock)
            {
                try
                {
                    /*
                                        _socket.SendTo(ws.CompressBuffer, 0, compressedLen,
                                            SocketFlags.None, player.EndPoint);
                    */
                    SendServerMessage(match, player, ServerMessageType.PlayerInput, ws.Payload);
                    ServerMetrics.PacketsSent.Add(1);
                }
                catch (SocketException ex)
                {
                    Log.SendFailed(_logger, player.PlayerIndex, player.MatchId, ex);
                    player.Disconnected = true;
                    return;
                }
            }

            player.LastSentTimestamp = ts;
            player.PendingPings[match.SequenceCounter] = ts;
        }

        /// <summary>
        /// Cold-path send: used for non-tick messages (connection replies, ping requests,
        /// start game, player config). Allocates normally — called infrequently.
        /// </summary>
        private uint SendServerMessage(
            MatchState match, PlayerInfo player, ServerMessageType type, object? payload)
        {
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
                    _logger.LogError("Send failed for player {Index}: {Err}",
                        player.PlayerIndex, ex.Message);
                    player.Disconnected = true;
                    return 0;
                }
            }

            return header.Sequence;
        }


        // ═══════════════════════════════════════════
        //  HTTP Integration (unchanged logic)
        // ═══════════════════════════════════════════

        private async Task<OVSMatchConfig?> FetchMatchConfigAsync(string matchId, string key)
        {
            string path = IsOVS ? Endpoints.OVSRegister : Endpoints.MVSIRegister;
            string url = BaseUrl + path;

            var requestBody = new { matchId, key, hostname = Utilities.Hostname };
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient.PostAsync(url, content);
                var body = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<OVSMatchConfig>(body);
            }
            catch (Exception ex)
            {
                Log.FetchConfigFailed(_logger, matchId, ex);
                return null;
            }
        }

        private async Task SendEndMatchAsync(string matchId, string key)
        {
            string path = IsOVS ? Endpoints.OVSEndMatch : Endpoints.MVSIEndMatch;
            string url = BaseUrl + path;

            var requestBody = new { matchId, key, hostname = Utilities.Hostname };
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                await _httpClient.PostAsync(url, content);
                Log.MatchEnded(_logger, matchId, url);
            }
            catch (Exception ex)
            {
                Log.EndMatchFailed(_logger, url, ex);
            }
        }

        // ═══════════════════════════════════════════
        //  Configuration
        // ═══════════════════════════════════════════

        private string GetBaseUrlFromEnv()
        {
            var url = Environment.GetEnvironmentVariable("OVS_SERVER") ?? "";
            IsOVS = !string.IsNullOrEmpty(url);

            if (string.IsNullOrEmpty(url))
            {
                IsOVS = false;
                Log.OVSNotSet(_logger);
                url = Environment.GetEnvironmentVariable("mvsi_server") ?? "";
                IsMVSI = !string.IsNullOrEmpty(url);
            }

            if (!string.IsNullOrEmpty(url) && url.EndsWith('/'))
                return url[..^1];

            if (!IsOVS && !IsMVSI)
                Log.NoServerConfigured(_logger);

            return url;
        }


        public static partial class Log
        {
            // ── Server Lifecycle ──

            [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
                Message = "Listening on UDP port {Port}")]
            public static partial void Listening(ILogger logger, ushort port);

            [LoggerMessage(EventId = 1001, Level = LogLevel.Information,
                Message = "OVS server started ({ServerType} on port: {port})")]
            public static partial void ServerStarted(ILogger logger, string serverType, ushort port);

            [LoggerMessage(EventId = 1002, Level = LogLevel.Information,
                Message = "OVS server stopped")]
            public static partial void ServerStopped(ILogger logger);

            [LoggerMessage(EventId = 1003, Level = LogLevel.Information,
                Message = "Server running. Press Ctrl+C to stop.")]
            public static partial void ServerRunning(ILogger logger);

            [LoggerMessage(EventId = 1004, Level = LogLevel.Information,
                Message = "Shutting down server...")]
            public static partial void ShuttingDown(ILogger logger);

            [LoggerMessage(EventId = 1005, Level = LogLevel.Information,
                Message = "Match data endpoint: {baseURL}")]
            public static partial void MatchEndpoint(ILogger logger, string baseURL);

            // ── Match Lifecycle ──

            [LoggerMessage(EventId = 1100, Level = LogLevel.Information,
                Message = "New Match: {MatchId}")]
            public static partial void NewMatch(ILogger logger, string matchId);

            [LoggerMessage(EventId = 1101, Level = LogLevel.Information,
                Message = "Match {MatchId} cleaned up (all players disconnected)")]
            public static partial void MatchCleanedUp(ILogger logger, string matchId);

            [LoggerMessage(EventId = 1102, Level = LogLevel.Information,
                Message = "Sent end match notice for Match ID {matchID} to URL: {url}")]
            public static partial void MatchEnded(ILogger logger, string matchId, string url);

            // ── Player Lifecycle ──

            [LoggerMessage(EventId = 1200, Level = LogLevel.Information,
                Message = "Player {PlayerIndex} joined match {matchID}")]
            public static partial void PlayerJoined(ILogger logger, ushort playerIndex, string matchID);

            [LoggerMessage(EventId = 1201, Level = LogLevel.Information,
                Message = "Player index {PlayerIndex} for matchID {matchID} timed out (no input for {Timeout}s)")]
            public static partial void PlayerTimedOut(ILogger logger, ushort playerIndex, string matchID, int timeout);

            [LoggerMessage(EventId = 1202, Level = LogLevel.Information,
                Message = "Player index {PlayerIndex} sent Disconnecting message for Match ID: {matchID}")]
            public static partial void PlayerDisconnecting(ILogger logger, ushort playerIndex, string matchID);

            // ── Ping Phase ──

            [LoggerMessage(EventId = 1300, Level = LogLevel.Information,
                Message = "Starting ping phase for Match ID: {matchID}")]
            public static partial void PingPhaseStarted(ILogger logger, string matchID);

            [LoggerMessage(EventId = 1301, Level = LogLevel.Information,
                Message = "Broadcasting players configuration for match {matchID}")]
            public static partial void BroadcastingPlayersConfig(ILogger logger, string matchID);

            // ── Rift ──

            [LoggerMessage(EventId = 1400, Level = LogLevel.Information,
                Message = "MatchID: {matchID} PIndex:{PlayerIndex} PING:{Ping} RIFT:{SmoothRift:F2} RAWRIFT:{RawRift:F2} clientFrame:{ClientFrame:F1} serverFrame:{ServerFrame}")]
            public static partial void RiftInfo(ILogger logger, string matchID, ushort playerIndex,
                short ping, float smoothRift, float rawRift, float clientFrame, uint serverFrame);

            // ── Tick Performance ──

            [LoggerMessage(EventId = 1500, Level = LogLevel.Information,
                Message = "Average tick interval: {AvgUs:F0} μs")]
            public static partial void TickPerformance(ILogger logger, double AvgUs);

            // ── Warnings ──

            [LoggerMessage(EventId = 2000, Level = LogLevel.Warning,
                Message = "DESYNC at frame {Frame}: player {PlayerA}={ChecksumA} vs player {PlayerB}={ChecksumB}")]
            public static partial void DesyncDetected(ILogger logger, uint frame,
                ushort playerA, string checksumA, ushort playerB, string checksumB);

            [LoggerMessage(EventId = 2001, Level = LogLevel.Warning,
                Message = "Bit-packing fallback: {Reason}")]
            public static partial void BitPackingFallback(ILogger logger, string reason);

            [LoggerMessage(EventId = 2002, Level = LogLevel.Warning,
                Message = "OVS_SERVER not set, checking mvsi_server")]
            public static partial void OVSNotSet(ILogger logger);

            [LoggerMessage(EventId = 2003, Level = LogLevel.Warning,
                Message = "Neither OVS_SERVER nor mvsi_server set")]
            public static partial void NoServerConfigured(ILogger logger);

            // ── Errors ──

            [LoggerMessage(EventId = 3000, Level = LogLevel.Error,
                Message = "UDP receive error: ")]
            public static partial void ReceiveError(ILogger logger, Exception exception);

            [LoggerMessage(EventId = 3001, Level = LogLevel.Error,
                Message = "Error handling message: ")]
            public static partial void HandleError(ILogger logger, Exception exception);

            [LoggerMessage(EventId = 3002, Level = LogLevel.Error,
                Message = "Send failed to player {PlayerIndex} for MatchID {matchID}: ")]
            public static partial void SendFailed(ILogger logger, ushort playerIndex, string matchID, Exception exception);

            [LoggerMessage(EventId = 3003, Level = LogLevel.Error,
                Message = "Failed to fetch match config for match: {matchID}")]
            public static partial void FetchConfigFailed(ILogger logger, string matchID, Exception exception);

            [LoggerMessage(EventId = 3004, Level = LogLevel.Error,
                Message = "Ping phase error for Match ID {matchID}: ")]
            public static partial void PingPhaseError(ILogger logger, string matchID, Exception exception);

            [LoggerMessage(EventId = 3005, Level = LogLevel.Error,
                Message = "Failed to POST end-match to {Url}, exception: ")]
            public static partial void EndMatchFailed(ILogger logger, string url, Exception exception);

            [LoggerMessage(EventId = 3006, Level = LogLevel.Error,
                Message = "Invalid JSON from {Path}")]
            public static partial void InvalidJson(ILogger logger, string path);
        }
    }
}
