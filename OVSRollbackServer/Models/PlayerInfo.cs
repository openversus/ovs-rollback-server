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
        public string MatchId { get; set; } = "";
        public ushort PlayerIndex { get; set; }

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

        public bool Emulated { get; set; }

        public static float ClampFloat(float value, float maxRange)
            => Math.Clamp(value, -maxRange, maxRange);
    }
}
