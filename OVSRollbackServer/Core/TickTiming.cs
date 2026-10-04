// TickTiming.cs: what the tick loop needs to know about this machine's sleep before it starts.
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace OVS.Rollback.Core
{
    /// <summary>
    /// The tick loop sleeps until shortly before each deadline, then yields and spins the rest of the way. How early it
    /// must stop sleeping depends on how late this machine's Thread.Sleep returns: tens of microseconds on Linux, several
    /// milliseconds on Windows at the default timer resolution (both Windows P2P hosts showed it, 2026-10-03). Measured
    /// once per process, before the first loop, rather than assumed; on Windows a 1 ms timer is requested first, which
    /// brings the overshoot down to about a millisecond.
    /// </summary>
    public static class TickTiming
    {
        private static readonly object s_lock = new();
        private static bool s_measured;

        /// <summary>The longest Thread.Sleep(1) overshoot seen in the calibration, in Stopwatch ticks; 0 before <see cref="Calibrate"/>.</summary>
        public static long SleepOvershootTicks { get; private set; }
        /// <summary>The median overshoot of the calibration, in milliseconds, for the log.</summary>
        public static double SleepOvershootMedianMs { get; private set; }
        /// <summary>Whether a 1 ms system timer was requested and granted (Windows only).</summary>
        public static bool FineTimer { get; private set; }

        /// <summary>
        /// The margin the loop keeps between the end of its sleep and the deadline, in ticks: the configured spin
        /// threshold, or the measured overshoot plus half a millisecond when that is more, so a sleep that ends as late
        /// as any seen in the calibration still ends before the deadline.
        /// </summary>
        public static long SpinThresholdFor(long configuredTicks)
        {
            long floor = SleepOvershootTicks + Stopwatch.Frequency / 2000;
            return Math.Max(configuredTicks, floor);
        }

        /// <summary>Requests the fine timer on Windows and measures the sleep overshoot. Runs once; later calls return at once.</summary>
        public static void Calibrate(ILogger logger)
        {
            lock (s_lock)
            {
                if (s_measured)
                {
                    return;
                }
                s_measured = true;

                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        FineTimer = timeBeginPeriod(1) == 0;
                    }
                    catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
                    {
                        FineTimer = false;
                    }
                }

                // Ten sleeps of one millisecond: about 10 ms on a precise system, up to 160 ms on a coarse one.
                const int samples = 10;
                var overshoot = new long[samples];
                long oneMs = Stopwatch.Frequency / 1000;
                for (int i = 0; i < samples; i++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    Thread.Sleep(1);
                    overshoot[i] = Math.Max(0, Stopwatch.GetTimestamp() - t0 - oneMs);
                }
                Array.Sort(overshoot);
                SleepOvershootTicks = overshoot[samples - 1];
                SleepOvershootMedianMs = overshoot[samples / 2] * 1000.0 / Stopwatch.Frequency;
                logger.LogInformation(
                    "Sleep calibration: Thread.Sleep(1) overshoots by {Median:F2} ms (median) / {Max:F2} ms (max) over {Samples} samples{Timer}; the tick loop stops sleeping at least {Margin:F2} ms before each deadline",
                    SleepOvershootMedianMs, SleepOvershootTicks * 1000.0 / Stopwatch.Frequency, samples,
                    OperatingSystem.IsWindows() ? (FineTimer ? " (1 ms Windows timer granted)" : " (1 ms Windows timer refused)") : "",
                    SpinThresholdFor(0) * 1000.0 / Stopwatch.Frequency);
            }
        }

        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern uint timeBeginPeriod(uint milliseconds);
    }
}
