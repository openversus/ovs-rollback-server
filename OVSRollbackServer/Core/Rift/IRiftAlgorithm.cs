// IRiftAlgorithm.cs
using OVS.Rollback.Configuration;
using OVS.Rollback.Models;

namespace OVS.Rollback.Core.Rift
{
    /// <summary>
    /// Turns the measured rift error into the value sent to a client as PlayerInput.Rift.
    /// </summary>
    /// <remarks>
    /// What the game client does with that value (final build, see the archive's rift.md): on every
    /// PlayerInput it discards its previous correction and derives a new one from this value alone.
    /// |Rift| &lt;= 1 means no correction. Above that, each rendered frame that simulates adds or
    /// removes trunc(|Rift|)/1200 s of game time, or trunc(3 × |Rift|)/1200 s above 10. Above 50 the
    /// client disconnects. It keeps correcting at the last value until told otherwise, so a stale value
    /// overshoots.
    ///
    /// Measurement, clamping (<see cref="RiftClamp"/>), variance, metrics and logging are shared and
    /// live in RollbackServer.CalcRiftVariableTick; an algorithm only smooths and reports.
    /// </remarks>
    public interface IRiftAlgorithm
    {
        string Name { get; }

        /// <summary>Whether to take a new measurement on this server frame.</summary>
        bool ShouldUpdate(uint serverFrame, ServerConfiguration config);

        /// <summary>Update <see cref="PlayerInfo.SmoothRift"/> from the current error. Not clamped here.</summary>
        void UpdateSmoothRift(PlayerInfo player, float riftError, ServerConfiguration config);

        /// <summary>The value to send, computed from the already clamped <see cref="PlayerInfo.SmoothRift"/>.</summary>
        float Report(PlayerInfo player, ServerConfiguration config);
    }

    /// <summary>Selects an algorithm by its configured name (RiftCalculation.Algorithm).</summary>
    public static class RiftAlgorithms
    {
        public static readonly IRiftAlgorithm Legacy = new LegacyRiftAlgorithm();
        public static readonly IRiftAlgorithm ClientMatched = new ClientMatchedRiftAlgorithm();
        /// <summary>Used when nothing is configured, and when the configured name is not recognised.</summary>
        public static readonly IRiftAlgorithm Default = ClientMatched;

        public static bool TryGet(string? name, out IRiftAlgorithm algorithm)
        {
            if (string.Equals(name, Legacy.Name, System.StringComparison.OrdinalIgnoreCase))
            {
                algorithm = Legacy;
                return true;
            }
            if (string.Equals(name, ClientMatched.Name, System.StringComparison.OrdinalIgnoreCase))
            {
                algorithm = ClientMatched;
                return true;
            }
            algorithm = Default;
            return false;
        }

        /// <summary>The configured algorithm, or <see cref="Default"/> when the name is unknown (Start() warns about that).</summary>
        public static IRiftAlgorithm Get(string? name)
        {
            TryGet(name, out var algorithm);
            return algorithm;
        }
    }

    /// <summary>
    /// The limit on the reported rift. The client's gain triples above 10 (see
    /// <see cref="IRiftAlgorithm"/>), and with a round trip of several frames in the loop that is what
    /// makes corrections overshoot, so the normal limit is MaxRiftDeviation (10). While the error is
    /// still larger than MaxRiftDeviationBoostAbove (60 frames, well beyond what the loop delay can
    /// overshoot through), MaxRiftDeviationBoost (20) is allowed so a long stall is still recovered
    /// quickly. The client disconnects above 50, so no limit may reach that.
    /// </summary>
    public static class RiftClamp
    {
        public static float Apply(float rift, float riftError, RiftCalculationSettings s)
        {
            float limit = s.MaxRiftDeviation;
            if (MathF.Abs(riftError) > s.MaxRiftDeviationBoostAbove)
                limit = MathF.Max(limit, s.MaxRiftDeviationBoost);
            return PlayerInfo.ClampFloat(rift, limit);
        }
    }
}
