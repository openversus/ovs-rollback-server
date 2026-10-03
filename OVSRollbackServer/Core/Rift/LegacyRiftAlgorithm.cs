// LegacyRiftAlgorithm.cs
using OVS.Rollback.Configuration;
using OVS.Rollback.Models;

namespace OVS.Rollback.Core.Rift
{
    /// <summary>
    /// The OVS server's original rift smoothing, moved here unchanged from
    /// RollbackServer.CalcRiftVariableTick. Updates every RiftUpdateInterval frames after
    /// RiftUpdateThreshold (every frame before it) and reports the smoothed value as is.
    /// </summary>
    public sealed class LegacyRiftAlgorithm : IRiftAlgorithm
    {
        public string Name => "Legacy";

        public bool ShouldUpdate(uint serverFrame, ServerConfiguration config)
            => !(serverFrame % config.RiftCalculation.RiftUpdateInterval != 0
                 && serverFrame > config.RiftCalculation.RiftUpdateThreshold);

        public void UpdateSmoothRift(PlayerInfo player, float riftError, ServerConfiguration config)
        {
            float RiftAlpha = config.RiftCalculation.RiftAlpha;

            if (config.RiftCalculation.UseAggressiveCorrection)
            {
                // Aggressive mode: snap quickly to reduce perceived delay
                if (MathF.Abs(riftError) < 0.2f)
                {
                    // Very close to target - hold steady
                    player.SmoothRift = riftError;
                }
                else if (MathF.Abs(riftError) < MathF.Abs(player.SmoothRift))
                {
                    // Converging - snap immediately
                    player.SmoothRift = riftError;
                }
                else
                {
                    // Diverging - use higher smoothing factor for faster response
                    float aggressiveAlpha = MathF.Min(RiftAlpha * 2.0f, 0.3f);
                    player.SmoothRift = aggressiveAlpha * riftError + (1f - aggressiveAlpha) * player.SmoothRift;
                }
            }
            else
            {
                // Conservative mode (original behavior)
                if (MathF.Abs(riftError) < 0.5f)
                {
                    player.SmoothRift *= 0.5f;
                    if (MathF.Abs(player.SmoothRift) < 0.01f)
                        player.SmoothRift = 0f;
                }
                else
                {
                    player.SmoothRift = RiftAlpha * riftError + (1f - RiftAlpha) * player.SmoothRift;
                }

                if (MathF.Abs(riftError) < MathF.Abs(player.SmoothRift))
                    player.SmoothRift = riftError;
            }
        }

        public float Report(PlayerInfo player, ServerConfiguration config) => player.SmoothRift;
    }
}
