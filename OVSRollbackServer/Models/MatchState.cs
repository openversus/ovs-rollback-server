// MatchState.cs
using System;
using System.Collections.Concurrent;
using OVS.Rollback.Core;

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

        // ── Per-player-slot input history: frame → input value ──
        public List<ConcurrentDictionary<uint, uint>> Inputs { get; set; } = [];

        // ── Desync detection: frame → (playerIndex → checksum) ──
        public ConcurrentDictionary<uint, ConcurrentDictionary<int, uint>> FrameChecksums { get; } = new();

        // ── Sequence & ping tracking ──
        public uint SequenceCounter { get; set; } = uint.MaxValue;
        public uint PingPhaseCount { get; set; }
        public uint PingPhaseTotal { get; set; }

        // ── Ping phase timer (prevents GC) ──
        public System.Threading.Timer? PingPhaseTimer { get; set; }

        // ── Tick loop control ──
        private int _tickRunning;
        public bool IsTickRunning => Volatile.Read(ref _tickRunning) == 1;
        public bool TryStartTick() => Interlocked.CompareExchange(ref _tickRunning, 1, 0) == 0;
        public void StopTick() => Volatile.Write(ref _tickRunning, 0);

        // ── NEW: Pre-allocated tick workspace (object pooling) ──
        public TickWorkspace? Workspace { get; set; }
    }
}
