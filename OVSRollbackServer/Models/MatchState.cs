// MatchState.cs
using System.Collections.Concurrent;

namespace Rollback.Models;

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

    // ── Sequence & ping tracking ──
    public uint SequenceCounter { get; set; } = uint.MaxValue; // wraps to 0 on first ++
    public uint PingPhaseCount { get; set; }
    public uint PingPhaseTotal { get; set; }

    // ── Tick loop control (atomic bool via Interlocked) ──
    private int _tickRunning;

    public bool IsTickRunning => Volatile.Read(ref _tickRunning) == 1;

    /// <summary>Atomically set tickRunning from false→true. Returns true if successful.</summary>
    public bool TryStartTick()
        => Interlocked.CompareExchange(ref _tickRunning, 1, 0) == 0;

    public void StopTick()
        => Volatile.Write(ref _tickRunning, 0);
}

