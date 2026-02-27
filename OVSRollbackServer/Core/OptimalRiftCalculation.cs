// OptimalRiftCalculation.cs
// Optimal rift calculation for platform fighter rollback netcode
//
// Key principles:
// 1. Update frequently (not throttled) for responsive feel
// 2. Target small positive rift (client runs slightly ahead)
// 3. Smooth adjustments to avoid jitter
// 4. Aggressive clamping to prevent runaway values

using OVS.Rollback.Models;
using System;
using System.Runtime.CompilerServices;

namespace OVS.Rollback.Core
{
    public static class OptimalRiftCalculation
    {
        // Target rift: client should run 2 frames ahead for responsive feel
        private const float TargetRift = 2.0f;
        
        // Target frame time in milliseconds (60 FPS)
        private const float TargetFrameTime = 1000f / 60f;
        
        // Smoothing factor: higher = more responsive, lower = more stable
        private const float RiftSmoothingAlpha = 0.15f;
        
        // Maximum allowed rift deviation (frames)
        private const float MaxRiftDeviation = 10f;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static float CalculateRift(PlayerInfo player, uint serverFrame)
        {
            // Calculate one-way network delay in frames
            float oneWayDelayMs = player.SmoothedPing * 0.5f;
            float oneWayFrames = oneWayDelayMs / TargetFrameTime;

            // Estimate where the client is NOW (their last reported frame + network delay)
            float estimatedClientNow = player.LastClientFrame + oneWayFrames;

            // Raw rift = how far ahead the client currently is
            float rawRift = estimatedClientNow - serverFrame;

            // Initialize on first calculation
            if (!player.RiftInit)
            {
                player.RiftInit = true;
                player.SmoothRift = rawRift - TargetRift;
                player.Rift = rawRift;
                return player.SmoothRift;
            }

            // Store raw rift for diagnostics
            player.Rift = rawRift;

            // Calculate error from target (negative = client is behind, positive = client is ahead)
            float riftError = rawRift - TargetRift;

            // Dead zone: snap to zero if very small (avoid floating point drift)
            if (MathF.Abs(riftError) < 0.1f)
            {
                player.SmoothRift = 0f;
            }
            // Converging: snap immediately if raw error is smaller than smoothed
            else if (MathF.Abs(riftError) < MathF.Abs(player.SmoothRift))
            {
                player.SmoothRift = riftError;
            }
            // Normal case: apply exponential smoothing
            else
            {
                player.SmoothRift = RiftSmoothingAlpha * riftError 
                                  + (1f - RiftSmoothingAlpha) * player.SmoothRift;
            }

            // Clamp to reasonable bounds
            player.SmoothRift = Math.Clamp(player.SmoothRift, -MaxRiftDeviation, MaxRiftDeviation);

            return player.SmoothRift;
        }
    }
}

