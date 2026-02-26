// InputPredictor.cs
using System;
using System.Collections.Generic;
using System.Text;

namespace OVS.Rollback.Core
{
    /// <summary>
    /// Predicts missing player input using input tendency weighting.
    ///
    /// Fighting-game heuristic:
    ///   - Directional inputs (d-pad/stick) change rapidly → decay to neutral
    ///     after a grace period.
    ///   - Button inputs (attack/block/etc.) are often held → preserve them
    ///     longer before decaying.
    ///
    /// This is only called in the server-side prediction branch when a player's
    /// input is missing beyond the grace period. It does NOT affect:
    ///   - Rift calculation
    ///   - Frame timing
    ///   - Wire format (the predicted uint is stored in the same Inputs map)
    /// </summary>
    public static class InputPredictor
    {
        // ── Bit layout (matches client input encoding) ──
        //
        //  Bits  0–3:  Directional inputs (d-pad / left stick)
        //  Bits  4–15: Button inputs (attack, block, special, etc.)
        //
        private const uint DirectionalMask = 0x000F; // bottom 4 bits
        private const uint ButtonMask = 0xFFF0; // upper 12 bits (within uint16 range)

        // ── Decay thresholds (in frames of missing input) ──
        //
        //  < DirectionalDecay : repeat last input verbatim (same as before)
        //  ≥ DirectionalDecay : release directional, keep buttons
        //  ≥ FullDecay        : release everything → neutral (0)
        //
        private const uint DirectionalDecayThreshold = 4;
        private const uint FullDecayThreshold = 15;

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
            // Phase 1: repeat verbatim (player likely still holding the same input)
            if (framesMissed < DirectionalDecayThreshold)
                return lastInput;

            // Phase 2: release directions, keep buttons
            // (players change direction frequently but tend to hold buttons)
            if (framesMissed < FullDecayThreshold)
                return lastInput & ButtonMask;

            // Phase 3: full neutral — we have no idea what the player is doing
            return 0;
        }
    }
}
