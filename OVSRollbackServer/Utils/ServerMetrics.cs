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
    ///   dotnet-counters monitor --name RollbackServer --counters OVS.Server.OVS
    /// </summary>

    public static class ServerMetrics
    {
        private static readonly Meter s_meter = new("OVS.Server.OVS", "2026.02.25");

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
