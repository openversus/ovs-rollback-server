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

        // ── Bots: filled in from match config at /ovs_register time. Bots
        //    occupy PlayerIndex slots but never UDP-connect, so we exclude
        //    them from ready-checks and from input-buffering iteration.
        public int NumBots { get; set; }
        public HashSet<int> BotIndices { get; set; } = new();

        // ── Per-player-slot input history: frame → input value ──
        public List<ConcurrentDictionary<uint, uint>> Inputs { get; set; } = [];

        // ── Desync detection: frame → (playerIndex → checksum) ──
        public ConcurrentDictionary<uint, ConcurrentDictionary<int, uint>> FrameChecksums { get; } = new();

        // ── Sequence & ping tracking ──
        // IMPORTANT: must start at 1, not 0. PingRingBuffer uses seq=0 as the
        // "empty slot" sentinel (uint[] zero-initialises to 0). If the first packet
        // ever sent carries sequence 0, TryRemove(0) will spuriously match every
        // uninitialised ring slot and return a bogus timestamp (~server uptime),
        // corrupting SmoothedPing and causing rift miscalculation ("sticky" inputs).
        public uint SequenceCounter { get; set; } = 1;
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

        // ── Input-cleanup round-robin cursor: advances one slot per tick ──
        public int CleanupCursor { get; set; }
    }
}
