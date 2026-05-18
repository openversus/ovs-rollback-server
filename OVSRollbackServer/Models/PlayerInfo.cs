// PlayerInfo.cs
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace OVS.Rollback.Models
{
    public class PlayerInfo
    {
        public readonly object Lock = new();

        // ── Connection state ──
        public volatile bool Disconnected;
        public IPEndPoint EndPoint { get; set; } = null!;
        public string MatchId { get; set; } = String.Empty;
        public ushort PlayerIndex { get; set; }
        public string PlayerId { get; set; } = String.Empty;
        public string PlayerName { get; set; } = String.Empty;
        public string PlayerCharacter { get; set; } = String.Empty;
        public bool IsSpectator { get; set; } = false;

        // ── Sequence tracking ──
        public uint LastSeqRecv { get; set; }
        public uint LastSeqSent { get; set; }
        public uint FirstInputServerFrame { get; set; } = uint.MaxValue;
        public bool HasCompletedWarmup(uint currentServerFrame, uint warmupFrames = 60)
            => FirstInputServerFrame != uint.MaxValue
               && (currentServerFrame - FirstInputServerFrame) >= warmupFrames;

        // ── Frame acknowledgement ──
        public List<uint> AckedFrames { get; set; } = [];
        public volatile bool Ready;

        // ── Timing (Stopwatch monotonic timestamps) ──
        public long LastInputTimestamp { get; set; } = Stopwatch.GetTimestamp();
        public long LastSentTimestamp { get; set; }

        // ── Ping smoothing ──
        public float SmoothedPing { get; set; }
        public float SmoothRift { get; set; }
        public bool PingInitialized { get; set; }
        public bool HasNewPing { get; set; }
        public bool RiftInit { get; set; }
        public short Ping { get; set; }

        // ── Client frame tracking ──
        public uint LastClientFrame { get; set; }
        public bool HasNewFrame { get; set; }
        public float Rift { get; set; }

        // ── Thread-safe maps (replaces ThreadSafeMap) ──
        public ConcurrentDictionary<uint, uint> MissedInputs { get; } = new();
        public ConcurrentDictionary<uint, long> PendingPings { get; } = new();

        // ── Desync detection ──
        public ConcurrentDictionary<uint, uint> Checksums { get; } = new();
        public int DesyncCount { get; set; }
        public uint FirstDesyncFrame { get; set; }

        // ── Connection stability (Welford online variance) ──
        // Updated every time CalcRiftVariableTick commits a ping/rift sample.
        // Welford's algorithm: maintains a running mean (M1) and sum of squared
        // deviations (M2) with no stored history and zero extra allocations.
        // Variance = M2 / count; lower = more stable connection / simulation.
        // Both accumulators are only written inside CalcRiftVariableTick while
        // player.Lock is held, so no additional synchronisation is needed.
        public long   PingVarianceSampleCount { get; set; }
        public double PingVarianceMean        { get; set; }
        public double PingVarianceM2          { get; set; }

        public long   RiftVarianceSampleCount { get; set; }
        public double RiftVarianceMean        { get; set; }
        public double RiftVarianceM2          { get; set; }

        // Minimum samples before variance is considered reliable enough to use
        // as a tiebreaker. Below this threshold the fallback (instantaneous score)
        // is used so early-match desyncs aren't decided by a single data point.
        public const int VarianceMinSamples = 10;

        /// <summary>Ping variance (population). Returns <see cref="double.MaxValue"/> when
        /// fewer than <see cref="VarianceMinSamples"/> samples have been collected.</summary>
        public double PingVariance => PingVarianceSampleCount >= VarianceMinSamples
            ? PingVarianceM2 / PingVarianceSampleCount
            : double.MaxValue;

        /// <summary>Rift variance (population). Returns <see cref="double.MaxValue"/> when
        /// fewer than <see cref="VarianceMinSamples"/> samples have been collected.</summary>
        public double RiftVariance => RiftVarianceSampleCount >= VarianceMinSamples
            ? RiftVarianceM2 / RiftVarianceSampleCount
            : double.MaxValue;

        // ── Future: Input rate limiting (not yet implemented) ──
        #pragma warning disable CS0649 // Field is never assigned to
        public int InputsSentThisSecond { get; set; }
        public long LastInputRateLimitReset { get; set; } = Stopwatch.GetTimestamp();
        #pragma warning restore CS0649

        public static float ClampFloat(float value, float maxRange)
            => Math.Clamp(value, -maxRange, maxRange);
    }
}
