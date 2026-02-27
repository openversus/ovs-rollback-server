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
        
        // Grace period: only 2 frames before going neutral
        private const uint GracePeriodFrames = 2;

        /// <summary>
        /// Predict input given the last known input and how many frames
        /// have been missing.
        /// </summary>
        /// <param name="lastInput">Last confirmed input from this player.</param>
        /// <param name="framesMissed">
        /// Number of consecutive frames without confirmed input (1-based).
        /// </param>
        /// <returns>Predicted input value for the current frame.</returns>
        public static uint Predict(uint lastInput, uint framesMissed)
        {
            // Short grace period: repeat verbatim for 2 frames
            if (framesMissed < GracePeriodFrames)
                return lastInput;

            // After grace period: go neutral immediately
            // This prevents phantom inputs in platform fighters
            return 0;
        }
    }
}
