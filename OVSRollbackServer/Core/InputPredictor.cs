// InputPredictor.cs
using System;
using System.Collections.Generic;
using System.Text;

namespace OVS.Rollback.Core
{
    /// <summary>
    /// Predicts missing player input using simplified platform fighter heuristic.
    ///
    /// For platform fighters:
    ///   - Inputs are critical and fast-paced
    ///   - Very short grace period (2 frames)
    ///   - Then immediately go neutral to avoid phantom inputs
    ///
    /// This avoids the "wrong direction aerial" problem where button inputs
    /// are preserved but directional inputs decay, causing attacks in 
    /// unintended directions.
    /// </summary>
    public static class InputPredictor
    {
        // All 32 bits used for input (no specific mask needed for prediction)
        // We simply repeat or go neutral

        // Default grace period when a caller doesn't specify one.
        private const uint DefaultGracePeriodFrames = 2;

        /// <summary>
        /// Predict input given the last known input and how many frames
        /// have been missing.
        /// </summary>
        /// <param name="lastInput">Last confirmed input from this player.</param>
        /// <param name="framesMissed">
        /// Number of consecutive frames without confirmed input (1-based).
        /// </param>
        /// <param name="graceFrames">
        /// Repeat the last input verbatim for this many frames before going
        /// neutral (config-driven; defaults to 2 to preserve prior behavior).
        /// </param>
        /// <returns>Predicted input value for the current frame.</returns>
        public static uint Predict(uint lastInput, uint framesMissed, uint graceFrames = DefaultGracePeriodFrames)
        {
            // framesMissed is 1-based. Preserve the corrected inclusive grace
            // semantics while allowing the duration to be configured.
            if (framesMissed <= graceFrames)
                return lastInput;

            // After grace period: go neutral immediately.
            // This prevents phantom directional inputs in platform fighters.
            return 0;
        }
    }
}
