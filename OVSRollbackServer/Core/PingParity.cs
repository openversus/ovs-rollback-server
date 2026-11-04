// PingParity.cs
using OVS.Rollback.Configuration;
using OVS.Rollback.Models;

namespace OVS.Rollback.Core
{
    /// <summary>
    /// The ping told to players under RiftCalculation.HostPingParity: one number for the whole match, formed from the
    /// remote active players' smoothed round trips (PingParityAggregate), multiplied (PingParityScale) and clamped
    /// to 255. Spectators, bots and players without a round trip yet are not counted, and neither is the host's
    /// game; its ping to the engine on its own machine is no path any packet would ever traverse.
    /// </summary>
    /// <remarks>
    /// The game raises its input delay from the ping it's told and NEVER lowers it (bug or design, I don't know,
    /// but that's what it does): delay 2 up to ~71 ms, 3 to 95, 4 to 119, 5 to 143, 6 to 166. So the same number told
    /// to every active player is the same input delay for every active player, and the scale chooses how much of the
    /// trip that delay covers. At 1 it covers the one-way trip between any player and the host, so nothing rolls
    /// back. At 0.5 each player is told what a relay midway between two players would have told them: the delay
    /// covers half the trip and rollback absorbs the rest, split evenly between the two sides.
    /// </remarks>
    public static class PingParity
    {
        public const string Slowest = "Slowest";
        public const string Mean = "Mean";
        public const string Smoothed = "Smoothed";
        public const string Median = "Median";
        /// <summary>One frame at 60 Hz, the rate the game's delay curve assumes.</summary>
        public const float SixtyHzFrameMs = 1000f / 60f;

        /// <summary>True when the name is Smoothed or Median (either case); the source is Smoothed for any other name.</summary>
        public static bool TryGetSource(string? name, out string source)
        {
            if (string.Equals(name, Median, StringComparison.OrdinalIgnoreCase))
            {
                source = Median;
                return true;
            }
            source = Smoothed;
            return string.Equals(name, Smoothed, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The median of the newest <paramref name="window"/> samples of a ring of round trips (ms, 0..255): the lower
        /// median of an even count. 0 when the ring is empty. No allocation: a count per millisecond on the stack.
        /// </summary>
        public static short MedianOf(short[] ring, int count, int next, int window)
        {
            int n = Math.Min(Math.Min(window, count), ring.Length);
            if (n <= 0)
                return 0;
            Span<int> perMs = stackalloc int[256];
            perMs.Clear();
            for (int i = 1; i <= n; i++)
            {
                int v = ring[(next - i + ring.Length) % ring.Length];
                perMs[Math.Clamp(v, 0, 255)]++;
            }
            int target = (n - 1) / 2;
            int seen = 0;
            for (int ms = 0; ms < 256; ms++)
            {
                seen += perMs[ms];
                if (seen > target)
                    return (short)ms;
            }
            return 255;
        }

        /// <summary>How many samples a window of <paramref name="windowMs"/> is at one probe per tick: 1..256.</summary>
        public static int WindowSamples(uint windowMs, float tickIntervalMs)
        {
            if (!(tickIntervalMs > 0f) || !float.IsFinite(tickIntervalMs))
                return 1;
            return (int)Math.Clamp(MathF.Round(windowMs / tickIntervalMs), 1f, 256f);
        }

        /// <summary>
        /// The factor that keeps the told value covering the same milliseconds at any tick rate: the game's delay curve
        /// counts frames of 1/60 s, so a match at a shorter frame is told its round trip scaled up by 60 Hz's frame over
        /// its own. 1 for 60 Hz or an unusable interval.
        /// </summary>
        public static float Compensation(float tickIntervalMs)
            => tickIntervalMs > 0f && float.IsFinite(tickIntervalMs) ? SixtyHzFrameMs / tickIntervalMs : 1f;

        /// <summary>
        /// One remote player's round trip as the parity number reads it: the smoothed ping (the exponential average),
        /// or the median of its newest <paramref name="windowSamples"/> raw round trips (PingParitySource). Call under
        /// the player's lock.
        /// </summary>
        public static float Sample(PlayerInfo player, RiftCalculationSettings settings, int windowSamples)
        {
            TryGetSource(settings.PingParitySource, out string source);
            if (source == Median && player.RecentPingCount > 0)
                return MedianOf(player.RecentPings, player.RecentPingCount, player.RecentPingNext, windowSamples);
            return player.SmoothedPing;
        }

        /// <summary>True when the name is Slowest or Mean (either case); the aggregate is Slowest for any other name.</summary>
        public static bool TryGetAggregate(string? name, out string aggregate)
        {
            if (string.Equals(name, Mean, StringComparison.OrdinalIgnoreCase))
            {
                aggregate = Mean;
                return true;
            }
            aggregate = Slowest;
            return string.Equals(name, Slowest, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The scale to apply: the configured one, or 1 when it is not a positive finite number.</summary>
        public static float Scale(float configured) => float.IsFinite(configured) && configured > 0f ? configured : 1f;

        /// <summary>
        /// The ping to tell, from the slowest remote round trip and the sum over <paramref name="count"/> remote round
        /// trips (ms); -1 when there is none yet, which leaves each player told its own measured ping. With
        /// PingParityTickRateCompensation the value is scaled by 60 Hz's frame over the match's
        /// (<paramref name="tickIntervalMs"/>), after the scale and before the 255 cap (which a compensated value
        /// reaches around a 230 ms round trip at 72 Hz, where the game's delay is at its limit of 10 anyway).
        /// </summary>
        public static short Told(float slowest, float sum, int count, RiftCalculationSettings settings, float tickIntervalMs = SixtyHzFrameMs)
        {
            if (count <= 0)
                return -1;
            TryGetAggregate(settings.PingParityAggregate, out string aggregate);
            float value = aggregate == Mean ? sum / count : slowest;
            value *= Scale(settings.PingParityScale);
            if (settings.PingParityTickRateCompensation)
                value *= Compensation(tickIntervalMs);
            // Rounded, not truncated: the game's delay thresholds are whole milliseconds, and a product like 191.99
            // is 192.
            return (short)Math.Min(MathF.Round(value), 255f);
        }
    }
}
