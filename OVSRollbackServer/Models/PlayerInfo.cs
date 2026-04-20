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
