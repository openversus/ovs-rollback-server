using System;
using System.Collections.Generic;
using System.Text;
// ServerMetrics.cs
using System.Diagnostics.Metrics;

namespace OVS.Rollback.Utils
{
    /// <summary>
    /// Read-only metric instruments. No-op when no listener is attached.
    /// Never modifies game state — safe to call from any code path.
    /// Centralized metrics for the rollback server.
    ///
    /// Monitor live with:
    ///   dotnet-counters monitor --name OVS.Rollback.Server --counters OVS.Rollback.Server
    /// </summary>

    public static class ServerMetrics
    {
        private static readonly Meter s_meter = new("OVS.Rollback.Server", "2026.02.27");

        // ── Tick loop ──
        public static readonly Counter<long> TicksProcessed =
            s_meter.CreateCounter<long>("rollback.ticks.processed");
        public static readonly Histogram<double> TickDurationUs =
            s_meter.CreateHistogram<double>("rollback.tick.duration_us", "μs");

        // ── Rift / Ping (recorded inside CalcRiftVariableTick, after clamp) ──
        public static readonly Histogram<double> RiftValue =
            s_meter.CreateHistogram<double>("rollback.rift.value");
        public static readonly Histogram<double> PingValue =
            s_meter.CreateHistogram<double>("rollback.ping.ms", "ms");
        public static readonly Histogram<double> RiftError =
            s_meter.CreateHistogram<double>("rollback.rift.error");
        public static readonly Counter<long> RiftCorrections =
            s_meter.CreateCounter<long>("rollback.rift.corrections");

        // ── Network ──
        public static readonly Counter<long> PacketsSent =
            s_meter.CreateCounter<long>("rollback.packets.sent");
        public static readonly Counter<long> PacketsReceived =
            s_meter.CreateCounter<long>("rollback.packets.received");

        // ── Input quality ──
        public static readonly Counter<long> InputMisses =
            s_meter.CreateCounter<long>("rollback.input.misses");
        public static readonly Counter<long> InputPredictions =
            s_meter.CreateCounter<long>("rollback.input.predictions");
        public static readonly Counter<long> BitPackFallbacks =
                s_meter.CreateCounter<long>("rollback.bitpack.fallbacks");

        // ── Input validation & security ──
        public static readonly Counter<long> InputsRateLimited =
            s_meter.CreateCounter<long>("rollback.inputs.rate_limited");
        public static readonly Counter<long> InputsRejectedFuture =
            s_meter.CreateCounter<long>("rollback.inputs.rejected_future");
        public static readonly Counter<long> InputsRejectedPast =
            s_meter.CreateCounter<long>("rollback.inputs.rejected_past");
        public static readonly Counter<long> InputDuplicates =
            s_meter.CreateCounter<long>("rollback.inputs.duplicates");

        // ── Desync detection ──
        public static readonly Counter<long> DesyncsDetected =
            s_meter.CreateCounter<long>("rollback.desyncs.detected");
        public static readonly Counter<long> ChecksumsProcessed =
            s_meter.CreateCounter<long>("rollback.checksums.processed");

        // ── Match lifecycle ──
        public static readonly Counter<long> MatchesStarted =
            s_meter.CreateCounter<long>("rollback.matches.started");
        public static readonly Counter<long> MatchesEnded =
            s_meter.CreateCounter<long>("rollback.matches.ended");
        public static readonly Counter<long> PlayersConnected =
            s_meter.CreateCounter<long>("rollback.players.connected");
        public static readonly Counter<long> PlayersDisconnected =
            s_meter.CreateCounter<long>("rollback.players.disconnected");
    }

}
