// MatchState.cs
using System;
using System.Collections.Concurrent;
using OVS.Rollback.Core;
using OVS.Rollback.Interfaces;

namespace OVS.Rollback.Models
{
    public class MatchState
    {
        public readonly object Lock = new();

        // ── Match identification ──
        public string MatchId { get; set; } = "";
        public string Key { get; set; } = "";

        // ── Players ──
        public ConcurrentDictionary<string, PlayerInfo> Players { get; } = new();

        // ── Match configuration ──
        public uint DurationInFrames { get; set; }
        public float TickIntervalMs { get; set; }
        public uint CurrentFrame { get; set; }
        public int MaxPlayers { get; set; }
        public int NumSpectators
        {
            get {
                if (null == Players || Players.IsEmpty)
                {
                    return 0;
                }

                int count = 0;
                foreach (var player in Players.Values)
                {
                    if (player.IsSpectator)
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        public int ActualPlayers => Players.Count - NumSpectators;

        // ── Match config (stored per-match so concurrent matches don't alias) ──
        public OVSMatchConfig? Config { get; set; }

        // ── Bots: filled in from match config at /ovs_register time. Bots
        //    occupy PlayerIndex slots but never UDP-connect, so we exclude
        //    them from ready-checks and from input-buffering iteration.
        public int NumBots { get; set; }
        public HashSet<int> BotIndices { get; set; } = new();

        // ── Per-player-slot input history: frame → input value ──
        public List<ConcurrentDictionary<uint, uint>> Inputs { get; set; } = [];

        // ── Desync detection: frame → (playerIndex → checksum) ──
        public ConcurrentDictionary<uint, ConcurrentDictionary<int, uint>> FrameChecksums { get; } = new();

        // ── Highest frame where every non-spectator, non-bot player agreed on the
        //    checksum. Sent back to clients as ChecksumAckFrame so they can free
        //    rollback history older than this frame. Written atomically. ──
        private uint _lastVerifiedFrame;
        public uint LastVerifiedFrame => Volatile.Read(ref _lastVerifiedFrame);
        public void TryAdvanceVerifiedFrame(uint frame)
        {
            uint current = Volatile.Read(ref _lastVerifiedFrame);
            while (frame > current)
            {
                uint observed = Interlocked.CompareExchange(ref _lastVerifiedFrame, frame, current);
                if (observed == current) break;
                current = observed;
            }
        }

        // ── Highest frame fully handled by checksum evaluation (verified or desynced).
        //    Prevents re-processing completed frames on subsequent ProcessChecksums calls,
        //    and anchors checksum pruning even when desyncs stall LastVerifiedFrame. ──
        private uint _lastHandledChecksumFrame;
        public uint LastHandledChecksumFrame => Volatile.Read(ref _lastHandledChecksumFrame);
        public void TryAdvanceHandledChecksumFrame(uint frame)
        {
            uint current = Volatile.Read(ref _lastHandledChecksumFrame);
            while (frame > current)
            {
                uint observed = Interlocked.CompareExchange(ref _lastHandledChecksumFrame, frame, current);
                if (observed == current) break;
                current = observed;
            }
        }

        // ── Sequence & ping tracking ──
        public uint SequenceCounter { get; set; } = uint.MaxValue;
        public uint PingPhaseCount { get; set; }
        public uint PingPhaseTotal { get; set; }

        // ── Ping phase timer (prevents GC) ──
        public System.Threading.Timer? PingPhaseTimer { get; set; }

        // ── Ping phase idempotence: 0 = not started, 1 = started ──
        private int _pingPhaseStarted;
        /// <summary>
        /// Returns true the first time it is called; false on every subsequent call.
        /// Ensures StartPingPhase executes exactly once per match even if multiple
        /// connection packets arrive simultaneously.
        /// </summary>
        public bool TryStartPingPhase() => Interlocked.CompareExchange(ref _pingPhaseStarted, 1, 0) == 0;

        // ── PlayersConfiguration broadcast idempotence: 0 = not sent, 1 = sent ──
        // Guards against the Timer disposal race where a queued callback fires after
        // the terminal tick has already broadcast the configuration.
        private int _playersConfigurationBroadcast;
        /// <summary>
        /// Returns true the first time it is called; false on every subsequent call.
        /// Ensures PlayersConfiguration is sent to clients exactly once per match.
        /// </summary>
        public bool TryBroadcastPlayersConfiguration() =>
            Interlocked.CompareExchange(ref _playersConfigurationBroadcast, 1, 0) == 0;

        // ── Tick loop control ──
        private int _tickRunning;
        public bool IsTickRunning => Volatile.Read(ref _tickRunning) == 1;
        public bool TryStartTick() => Interlocked.CompareExchange(ref _tickRunning, 1, 0) == 0;
        public void StopTick() => Volatile.Write(ref _tickRunning, 0);

        // ── NEW: Pre-allocated tick workspace (object pooling) ──
        public TickWorkspace? Workspace { get; set; }
    }
}
