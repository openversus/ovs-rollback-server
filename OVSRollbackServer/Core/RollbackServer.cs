// RollbackServer.cs
using Rollback.Models;
using Rollback.Utils;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Rollback.Core;

public sealed class RollbackServer : IAsyncDisposable
{
    // ── Constants (from rollback_server.cpp top-level) ──
    private const float TargetFrameTime = 1000f / 60f;
    private const float PingAlpha = 0.1f;
    private const float RiftAlpha = 0.05f;
    private const byte MaxInputsPerFrame = 30;
    private const int DisconnectTimeout = 30; // seconds

    // ── Infrastructure ──
    private readonly ushort _port;
    private readonly int _maxPlayers;
    private readonly Socket _socket;
    private readonly HttpClient _httpClient = new();
    private readonly string _logPrefix = Utilities.LogPrefix;

    // ── State maps ──
    private readonly ConcurrentDictionary<string, MatchState> _matches = new();
    private readonly ConcurrentDictionary<string, PlayerInfo> _players = new();

    // ── Match creation lock (replaces unique_lock on matches_.mutex_) ──
    private readonly SemaphoreSlim _matchCreationLock = new(1, 1);

    // ── Lifecycle ──
    private volatile bool _running;
    private Task? _udpTask;

    // ── Configuration ──
    public string BaseUrl { get; private set; } = "";
    public bool IsOvs { get; private set; }
    public bool IsMvsi { get; private set; }

    private static class Endpoints
    {
        public const string OvsRegister = "/ovs_register";
        public const string OvsEndMatch = "/ovs_end_match";
        public const string MvsiRegister = "/mvsi_register";
        public const string MvsiEndMatch = "/mvsi_end_match";
    }

    // ═══════════════════════════════════════════
    //  Constructor / Lifecycle
    // ═══════════════════════════════════════════

    public RollbackServer(ushort port = Constants.GameServerPort, int maxPlayers = Constants.MaxPlayers)
    {
        _port = port;
        _maxPlayers = maxPlayers;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        BaseUrl = GetBaseUrlFromEnv();
        if (string.IsNullOrEmpty(BaseUrl))
            throw new InvalidOperationException(
                "No base URL configured. Please set the OVS_SERVER environment variable.");

        Console.WriteLine($"{_logPrefix}Initializing rollback server on port {port}");
    }

    public void Start()
    {
        if (_running) return;
        _running = true;

        _socket.Bind(new IPEndPoint(IPAddress.Any, _port));
        _udpTask = Task.Run(RunUdpServerAsync);

        string serverType = IsOvs ? "OVS" : (IsMvsi ? "MVSI" : "Unknown");
        Console.WriteLine($"{_logPrefix}Rollback server started");
        Console.WriteLine(
            $"{_logPrefix}Match data will be fetched from and reported to {serverType} server at: {BaseUrl}");
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

        Console.WriteLine($"{_logPrefix}Rollback server stopped");
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

                // Fire-and-forget (co_spawn detached)
                _ = HandleMessageAsync(data, result.ReceivedBytes, remote);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{_logPrefix}Error in UDP server: {ex.Message}");
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
            var decompressed = CompressionHelper.Decompress(buffer.AsSpan(0, length));
            var clientMsg = MessageSerializer.ParseClientMessage(decompressed);
            if (clientMsg is null) return;

            var header = clientMsg.Value.Header;
            var type = header.Type;

            MatchState? match = null;
            PlayerInfo? player = null;

            if (type == ClientMessageType.NewConnection)
            {
                var payload = (NewConnectionPayload)clientMsg.Value.Payload;
                player = await HandleNewConnectionAsync(payload, remote);
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

            // Filter out-of-order / duplicate packets
            if (header.Sequence <= player.LastSeqRecv) return;
            player.LastSeqRecv = header.Sequence;

            // Handle QualityData ping measurement
            if (type == ClientMessageType.QualityData)
            {
                var qPayload = (QualityDataPayload)clientMsg.Value.Payload;
                if (player.PendingPings.TryRemove(qPayload.ServerMessageSequenceNumber, out long ts))
                {
                    player.Ping = (short)Stopwatch.GetElapsedTime(ts).TotalMilliseconds;
                }
            }

            // Dispatch by type
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
                    Console.WriteLine(
                        $"{_logPrefix}Player index {player.PlayerIndex} sent Disconnecting message");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{_logPrefix}Error handling message: {ex.Message}");
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

        // ── Get or create match (atomic via semaphore, matching C++ unique_lock on matches_.mutex_) ──
        MatchState? match;
        await _matchCreationLock.WaitAsync();
        try
        {
            if (!_matches.TryGetValue(matchData.MatchId, out match))
            {
                Console.WriteLine($"{_logPrefix}New Match : {matchData.MatchId}");
                var config = await FetchMatchConfigFromServerAsync(matchData.MatchId, matchData.Key);
                if (config is null)
                {
                    Console.Error.WriteLine($"{_logPrefix}Failed to fetch match config from server");
                    return null;
                }

                match = new MatchState
                {
                    MatchId = matchData.MatchId,
                    Key = matchData.Key,
                    DurationInFrames = config.MatchDuration,
                    TickIntervalMs = 1000f / 60f,
                    CurrentFrame = 0,
                    MaxPlayers = config.MaxPlayers,
                    PingPhaseCount = 0,
                    PingPhaseTotal = 20,
                    SequenceCounter = uint.MaxValue, // first ++ wraps to 0
                    Inputs = Enumerable.Range(0, config.MaxPlayers)
                        .Select(_ => new ConcurrentDictionary<uint, uint>()).ToList()
                };
                _matches[matchData.MatchId] = match;
            }
        }
        finally
        {
            _matchCreationLock.Release();
        }

        // ── Return existing player if reconnecting ──
        if (_players.TryGetValue(key, out var existing))
            return existing;

        // ── Create new player ──
        var newPlayer = new PlayerInfo
        {
            EndPoint = remote,
            MatchId = matchData.MatchId,
            PlayerIndex = payload.PlayerData.PlayerIndex,
            LastSeqRecv = 0,
            LastSeqSent = 0,
            AckedFrames = new List<uint>(new uint[match.MaxPlayers]),
            Ping = 0,
            Ready = debug,
            LastClientFrame = 0,
            LastInputTimestamp = Stopwatch.GetTimestamp(),
            Rift = 0,
            Emulated = debug
        };

        match.Players[key] = newPlayer;
        _players[key] = newPlayer;
        Console.WriteLine($"{_logPrefix}Player index {payload.PlayerData.PlayerIndex} joined");

        // ── Send connection reply (fire-and-forget) ──
        var reply = new NewConnectionReplyPayload
        {
            Success = 0,
            MatchNumPlayers = (byte)match.Players.Count,
            PlayerIndex = (byte)newPlayer.PlayerIndex,
            MatchDurationInFrames = match.DurationInFrames,
            IsValidationServerDebugMode = 0
        };
        _ = SendServerMessageAsync(match, newPlayer, ServerMessageType.NewConnectionReply, reply);

        // ── Start ping phase once all players are connected ──
        if (match.Players.Count == match.MaxPlayers)
            StartPingPhase(match);

        return newPlayer;
    }

    // ═══════════════════════════════════════════
    //  Ping Phase
    // ═══════════════════════════════════════════

    private void StartPingPhase(MatchState match)
    {
        Console.WriteLine($"{_logPrefix}Starting Ping Phase");

        _ = Task.Run(async () =>
        {
            try
            {
                await BroadcastRequestQualityAsync(match);
                match.PingPhaseCount++;

                for (uint i = 1; i < match.PingPhaseTotal && _running; i++)
                {
                    await Task.Delay(50);
                    await BroadcastRequestQualityAsync(match);
                    match.PingPhaseCount++;
                }

                await BroadcastPlayersConfigurationAsync(match);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{_logPrefix}Exception in ping phase: {ex.Message}");
            }
        });
    }

    private async Task BroadcastRequestQualityAsync(MatchState match)
    {
        long ts = Stopwatch.GetTimestamp();

        foreach (var (_, player) in match.Players.ToArray())
        {
            if (player.Disconnected) continue;

            var payload = new RequestQualityDataPayload { Ping = player.Ping };
            uint seq = await SendServerMessageAsync(
                match, player, ServerMessageType.RequestQualityData, payload);

            player.PendingPings[seq] = ts;
        }
    }

    private async Task BroadcastPlayersConfigurationAsync(MatchState match)
    {
        Console.WriteLine($"{_logPrefix}broadcastPlayersConfiguration");
        var snapshot = match.Players.ToArray();

        foreach (var (_, player) in snapshot)
        {
            if (player.Disconnected) continue;

            var payload = new PlayersConfigurationDataPayload
            {
                NumPlayers = (byte)snapshot.Length,
                ConfigValues = Enumerable.Range(0, match.MaxPlayers)
                    .Select(i => new ushort[] { 0, 256, 513, 769 }[i % 4]).ToList()
            };

            await SendServerMessageAsync(
                match, player, ServerMessageType.PlayersConfigurationData, payload);
        }
    }

    // ═══════════════════════════════════════════
    //  Input & Acknowledgement Handlers
    // ═══════════════════════════════════════════

    private void HandlePlayerInputAck(MatchState match, PlayerInfo player, PlayerInputAckPayload payload)
    {
        lock (player.Lock)
        {
            // Update acked frames
            for (int i = 0; i < payload.AckFrame.Count && i < player.AckedFrames.Count; i++)
            {
                uint acked = payload.AckFrame[i];
                if (acked != 0 && player.AckedFrames[i] < acked)
                    player.AckedFrames[i] = acked;
            }

            // Compute RTT from pending ping
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
        var snapshot = match.Players.ToArray();

        bool allReady = snapshot.All(p => p.Value.Ready);

        if (allReady)
        {
            foreach (var (_, p) in snapshot)
                _ = SendServerMessageAsync(match, p, ServerMessageType.StartGame, null);

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
            histMap.TryAdd(f, payload.InputPerFrame[i]); // TryAdd = skip if exists
        }
    }

    // ═══════════════════════════════════════════
    //  Rift / Ping Calculation
    // ═══════════════════════════════════════════

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

        if (player.SmoothRift > 1 || player.SmoothRift < -1 || player.SmoothedPing > 254)
        {
            Console.WriteLine(
                $"{_logPrefix}PIndex:{player.PlayerIndex} PING:{player.Ping} " +
                $"RIFT:{player.SmoothRift} RAWRIFT:{player.Rift} " +
                $"clientFrame:{predictedClientFrame} serverFrame:{serverFrame}");
        }
    }

    // ═══════════════════════════════════════════
    //  Tick Loop
    // ═══════════════════════════════════════════

    private void StartTickLoop(MatchState match)
    {
        if (!match.TryStartTick()) return;
        _ = Task.Run(() => RunTickLoopAsync(match));
    }

    private async Task RunTickLoopAsync(MatchState match)
    {
        long targetIntervalTicks =
            (long)(match.TickIntervalMs / 1000.0 * Stopwatch.Frequency);
        long startTime = Stopwatch.GetTimestamp();
        long nextTickTime = startTime + targetIntervalTicks;
        long accumulatedError = 0;

        int tickCount = 0;
        long monitorStart = Stopwatch.GetTimestamp();

        while (match.IsTickRunning && _running)
        {
            // ── Process tick ──
            await TickAsync(match);

            // ── Check if all players disconnected → cleanup ──
            var playerKeys = new List<string>();
            bool allDisconnected = true;
            foreach (var (k, p) in match.Players.ToArray())
            {
                playerKeys.Add(k);
                if (!p.Disconnected) { allDisconnected = false; break; }
            }

            if (allDisconnected)
            {
                await SendEndMatchAsync(match.MatchId, match.Key);
                match.StopTick();
                foreach (var k in playerKeys) _players.TryRemove(k, out _);
                match.Players.Clear();
                foreach (var inputMap in match.Inputs) inputMap.Clear();
                _matches.TryRemove(match.MatchId, out _);
                Console.WriteLine(
                    $"{_logPrefix}Match {match.MatchId} cleaned up (all players disconnected)");
                break;
            }

            // ── Frame counter from absolute elapsed time ──
            long now = Stopwatch.GetTimestamp();
            long elapsed = now - startTime;
            match.CurrentFrame = (uint)(elapsed / targetIntervalTicks);

            // ── Drift compensation ──
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
                continue; // behind schedule — run next tick immediately
            }

            // ── Wait ──
            int waitMs = (int)(waitTicks * 1000 / Stopwatch.Frequency);
            if (waitMs > 0)
            {
                try { await Task.Delay(waitMs); }
                catch (OperationCanceledException) { break; }
            }

            // ── Measure timing error ──
            long afterWait = Stopwatch.GetTimestamp();
            long timerError = (afterWait - now) - waitTicks;
            accumulatedError += timerError;

            // ── Performance monitoring ──
            tickCount++;
            if (tickCount >= 500)
            {
                var avg = Stopwatch.GetElapsedTime(monitorStart).TotalMicroseconds / tickCount;
                Console.WriteLine($"{_logPrefix}  Average tick interval: {avg:F0}");
                tickCount = 0;
                monitorStart = Stopwatch.GetTimestamp();
            }
        }
    }

    private async Task TickAsync(MatchState match)
    {
        var playersSnapshot = match.Players.ToArray();
        uint serverFrame = match.CurrentFrame;

        // ── Rift recalc & disconnect timeout ──
        foreach (var (_, player) in playersSnapshot)
        {
            lock (player.Lock)
            {
                CalcRiftVariableTick(player, serverFrame);

                if (!player.Disconnected &&
                    Stopwatch.GetElapsedTime(player.LastInputTimestamp).TotalSeconds > DisconnectTimeout)
                {
                    player.Disconnected = true;
                    Console.WriteLine(
                        $"{_logPrefix}Player index {player.PlayerIndex} timed out (no input > {DisconnectTimeout}s)");
                    continue;
                }
                if (player.Disconnected) continue;
            }
        }

        // ── Wait for minimum inputs before sending ──
        bool needMoreInputs = match.Inputs.Any(im => im.Count < 10);
        if (needMoreInputs)
        {
            foreach (var (_, recipient) in playersSnapshot)
                await SendServerMessageAsync(match, recipient, ServerMessageType.StartGame, null);
            return;
        }

        // ── Build per-client payload and send ──
        foreach (var (_, recipient) in playersSnapshot)
        {
            var startFrame = new List<uint>(new uint[match.MaxPlayers]);
            var numFrames = new List<byte>(new byte[match.MaxPlayers]);
            var inputPerFrame = new List<List<uint>>();
            for (int i = 0; i < match.MaxPlayers; i++) inputPerFrame.Add([]);
            ushort numPredictedOverrides = 0;

            List<uint> ackedFrames;
            uint lastClientFrame;
            short ping;
            float smoothRift;
            lock (recipient.Lock)
            {
                ackedFrames = new List<uint>(recipient.AckedFrames);
                lastClientFrame = recipient.LastClientFrame;
                ping = recipient.Ping;
                smoothRift = recipient.SmoothRift;
            }

            // For each peer, decide what frames to send
            foreach (var (_, peer) in playersSnapshot)
            {
                int idx = peer.PlayerIndex;
                var histMap = new Dictionary<uint, uint>(match.Inputs[idx]);
                uint lastAck = ackedFrames[idx];
                uint nextFrame = lastAck + 1;

                recipient.MissedInputs.TryGetValue((uint)idx, out uint missedCount);

                if (histMap.ContainsKey(nextFrame))
                {
                    // ── Have the next real input → send as many consecutive as possible ──
                    byte sentCount = 0;
                    startFrame[idx] = nextFrame;
                    uint f = nextFrame;
                    while (histMap.ContainsKey(f) && sentCount < MaxInputsPerFrame)
                    {
                        inputPerFrame[idx].Add(histMap[f]);
                        numFrames[idx]++;
                        f++;
                        sentCount++;
                    }
                    recipient.MissedInputs[(uint)idx] = 0;
                }
                else if (missedCount < 10)
                {
                    // ── Missing but within grace period → resend last known ──
                    startFrame[idx] = lastAck;
                    recipient.MissedInputs[(uint)idx] = missedCount + 1;
                    uint lastVal = histMap.TryGetValue(lastAck, out var v) ? v : 0;
                    inputPerFrame[idx].Add(lastVal);
                    numFrames[idx] = 1;
                }
                else
                {
                    // ── Too many misses → predict (repeat last input) ──
                    startFrame[idx] = nextFrame;
                    uint predictedCount = 0;
                    uint f = nextFrame;
                    uint lastVal = histMap.TryGetValue(lastAck, out var v2) ? v2 : 0;

                    while (f < lastClientFrame && predictedCount < MaxInputsPerFrame)
                    {
                        match.Inputs[idx][f] = lastVal;
                        inputPerFrame[idx].Add(lastVal);
                        predictedCount++;
                        f++;
                    }
                    numFrames[idx] = (byte)predictedCount;
                    numPredictedOverrides = (ushort)predictedCount;
                }
            }

            var playerInputPayload = new PlayerInputPayload
            {
                NumPlayers = (byte)match.Players.Count,
                StartFrame = startFrame,
                NumFrames = numFrames,
                NumPredictedOverrides = numPredictedOverrides,
                NumZeroedOverrides = 0,
                Ping = ping,
                PacketsLossPercent = 0,
                Rift = smoothRift,
                ChecksumAckFrame = 0,
                InputPerFrame = inputPerFrame
            };

            long ts = Stopwatch.GetTimestamp();
            await SendPlayerInputAsync(match, recipient, playerInputPayload);
            recipient.PendingPings[match.SequenceCounter] = ts;
        }

        // ── Cleanup old input history every 200 frames ──
        if (match.CurrentFrame % 200 == 0)
        {
            foreach (var histMap in match.Inputs)
            {
                if (histMap.Count <= 150) continue;
                var frames = histMap.Keys.OrderBy(k => k).ToList();
                int toRemove = frames.Count - 150;
                for (int i = 0; i < toRemove; i++)
                    histMap.TryRemove(frames[i], out _);
            }
        }
    }

    // ═══════════════════════════════════════════
    //  Sending
    // ═══════════════════════════════════════════

    private async Task SendPlayerInputAsync(
        MatchState match, PlayerInfo player, PlayerInputPayload payload)
    {
        player.LastSentTimestamp = Stopwatch.GetTimestamp();
        await SendServerMessageAsync(match, player, ServerMessageType.PlayerInput, payload);
    }

    private async Task<uint> SendServerMessageAsync(
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

        try
        {
            await _socket.SendToAsync(compressed, SocketFlags.None, player.EndPoint);
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine(
                $"{_logPrefix}Send failed for player {player.PlayerIndex}: {ex.Message}");
            player.Disconnected = true;
            return 0;
        }

        return header.Sequence;
    }

    // ═══════════════════════════════════════════
    //  HTTP Integration (replaces libcurl)
    // ═══════════════════════════════════════════

    private async Task<OvsMatchConfig?> FetchMatchConfigFromServerAsync(
        string matchId, string key)
    {
        string path = IsOvs ? Endpoints.OvsRegister : Endpoints.MvsiRegister;
        string url = BaseUrl + path;

        var requestBody = new { matchId, key, hostname = Utilities.Hostname };
        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await _httpClient.PostAsync(url, content);
            var body = await response.Content.ReadAsStringAsync();
            var config = JsonSerializer.Deserialize<OvsMatchConfig>(body);
            if (config is null)
            {
                Console.Error.WriteLine($"{_logPrefix}Invalid JSON from {path}");
                return null;
            }
            return config;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{_logPrefix}Failed to POST to {url}: {ex.Message}");
            return null;
        }
    }

    private async Task SendEndMatchAsync(string matchId, string key)
    {
        string path = IsOvs ? Endpoints.OvsEndMatch : Endpoints.MvsiEndMatch;
        string url = BaseUrl + path;

        var requestBody = new { matchId, key, hostname = Utilities.Hostname };
        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            await _httpClient.PostAsync(url, content);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{_logPrefix}Failed to POST to {url}: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════
    //  Configuration
    // ═══════════════════════════════════════════

    private string GetBaseUrlFromEnv()
    {
        var url = Environment.GetEnvironmentVariable("OVS_SERVER") ?? "";
        IsOvs = !string.IsNullOrEmpty(url);

        if (string.IsNullOrEmpty(url))
        {
            IsOvs = false;
            Console.Error.WriteLine(
                $"{_logPrefix}Warning: OVS_SERVER not set, checking \"mvsi_server\"");
            url = Environment.GetEnvironmentVariable("mvsi_server") ?? "";
            IsMvsi = !string.IsNullOrEmpty(url);
        }

        if (!string.IsNullOrEmpty(url) && url.EndsWith('/'))
            return url[..^1];

        if (!IsOvs && !IsMvsi)
        {
            Console.Error.WriteLine(
                $"{_logPrefix}Warning: Neither OVS_SERVER nor mvsi_server set, cannot continue.");
        }

        return url;
    }
}

