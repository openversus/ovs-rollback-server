// PlayerInfo.cs
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;

namespace OVS.Rollback.Models
{
    /// <summary>Equality comparer for <see cref="IPEndPoint"/> suitable for use as a dictionary key.</summary>
    public sealed class IPEndPointComparer : IEqualityComparer<IPEndPoint>
    {
        public static readonly IPEndPointComparer Instance = new();
        public bool Equals(IPEndPoint? x, IPEndPoint? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return x.Port == y.Port && x.Address.Equals(y.Address);
        }
        public int GetHashCode(IPEndPoint obj) => HashCode.Combine(obj.Address, obj.Port);
    }

    /// <summary>
    /// Zero-allocation fixed-size ring buffer for in-flight ping timestamps.
    /// Maps sequence numbers → Stopwatch timestamps for RTT measurement.
    ///
    /// Design constraints:
    ///   • Written from the tick thread (SendPlayerInput) and the ping-phase timer thread.
    ///   • Read/removed from the UDP receive thread (HandlePlayerInputAck, QualityData).
    ///   • Capacity = 64 (power-of-2): covers ~1.07 s of pings at 60 fps, well beyond
    ///     any realistic RTT.  Oldest slot is silently evicted when the ring is full —
    ///     acceptable because an un-acked ping simply produces no RTT sample.
    ///   • Thread-safety: Interlocked CAS on a "dirty" flag per slot prevents torn writes;
    ///     a reader that sees Dirty==1 spins for at most one store (< 10 ns on modern x86).
    ///
    /// Spectators share the same ring as active players — they receive pings identically.
    /// Sizing at 64 supports up to 8 players (4 active + 4 spectators) with 8 in-flight
    /// pings per player before eviction, which is well above the production ping rate.
    /// </summary>
    public sealed class PingRingBuffer
    {
        private const int Capacity = 64;           // must be power-of-2
        private const int Mask     = Capacity - 1;

        // Parallel arrays; index is (writeHead & Mask).
        private readonly uint[] _seqs       = new uint[Capacity];
        private readonly long[] _timestamps = new long[Capacity];
        // Per-slot dirty flag: 0 = stable, 1 = write in progress.
        private readonly int[]  _dirty      = new int[Capacity];

        private int _writeHead; // Interlocked; monotonically increasing

        /// <summary>
        /// Store <paramref name="seq"/> → <paramref name="ts"/> in the next ring slot.
        /// Lock-free; evicts the oldest entry when the ring is full.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(uint seq, long ts)
        {
            int slot = Interlocked.Increment(ref _writeHead) & Mask;

            // Mark slot dirty, write, clear dirty — readers spin if they catch us mid-write.
            Interlocked.Exchange(ref _dirty[slot], 1);
            _seqs[slot]       = seq;
            _timestamps[slot] = ts;
            Interlocked.Exchange(ref _dirty[slot], 0);
        }

        /// <summary>
        /// Remove the entry with sequence number <paramref name="seq"/> and return its
        /// timestamp via <paramref name="ts"/>.  Returns <c>false</c> if not found
        /// (already evicted or never inserted).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryRemove(uint seq, out long ts)
        {
            for (int i = 0; i < Capacity; i++)
            {
                if (_seqs[i] != seq) continue;

                // Spin while the writer is mid-store on this slot (extremely rare).
                while (Volatile.Read(ref _dirty[i]) == 1) { /* spin */ }

                // Re-check after the spin — the writer may have replaced it.
                if (_seqs[i] != seq) continue;

                ts = _timestamps[i];
                // Invalidate slot: use seq=0 as sentinel (sequence numbers start at uint.MaxValue
                // in production and increment, so 0 is never a valid in-flight sequence).
                _seqs[i] = 0;
                return true;
            }
            ts = 0;
            return false;
        }

        /// <summary>Indexer write: <c>buffer[seq] = ts</c> — mirrors ConcurrentDictionary API.</summary>
        public long this[uint seq]
        {
            set => Add(seq, value);
        }
    }

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

        // ── Missed-input counters: one slot per peer PlayerIndex.
        //    Plain array — only ever written from the single-threaded Tick loop,
        //    sized at match creation via InitMissedInputs(). Zero-allocation.
        public uint[] MissedInputs { get; private set; } = [];
        public void InitMissedInputs(int numPlayers)
            => MissedInputs = new uint[numPlayers];

        // ── Thread-safe maps (replaces ThreadSafeMap) ──
        public PingRingBuffer PendingPings { get; } = new();

        // ── Future: Desync detection (not yet implemented) ──
        #pragma warning disable CS0649 // Field is never assigned to
        public ConcurrentDictionary<uint, uint> Checksums { get; } = new();
        public int DesyncCount { get; set; }
        public uint FirstDesyncFrame { get; set; }
        #pragma warning restore CS0649

        // ── Future: Input rate limiting (not yet implemented) ──
        #pragma warning disable CS0649 // Field is never assigned to
        public int InputsSentThisSecond { get; set; }
        public long LastInputRateLimitReset { get; set; } = Stopwatch.GetTimestamp();
        #pragma warning restore CS0649

        public static float ClampFloat(float value, float maxRange)
            => Math.Clamp(value, -maxRange, maxRange);
    }
}
