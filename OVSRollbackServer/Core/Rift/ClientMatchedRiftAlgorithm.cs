// ClientMatchedRiftAlgorithm.cs
using OVS.Rollback.Configuration;
using OVS.Rollback.Models;

namespace OVS.Rollback.Core.Rift
{
    /// <summary>
    /// Rift tuned to how the game client applies it (see <see cref="IRiftAlgorithm"/>). Chosen with a
    /// closed-loop simulation of the client's correction law, calibrated against production logs
    /// (archive: targets/multiversus/netcode/rift-sim/). Against Legacy with the same clamp it
    /// recovers from a 30-frame hitch faster (1.3 s vs 2.3 s at 144 fps / 140 ms) with far less
    /// overshoot, and is as quiet in steady play.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A fresh value on every frame: the client acts on the last value it was sent, so anything
    /// stale keeps driving it after the error has gone.</item>
    /// <item>When the error changes sign the old value points the wrong way, so it is replaced, not
    /// blended. Legacy blends it, which is how a client 22 frames ahead was still told it was behind
    /// (match 6aa4267257a29d0012929f06, 2026-09-11).</item>
    /// <item>Small errors (under SmallErrorBelow frames) are smoothed with SmallErrorAlpha, because a
    /// single measurement carries about a frame of arrival jitter.</item>
    /// <item>Hysteresis around the client's 1-frame deadband: start reporting above HysteresisEnter,
    /// report exactly 0 once below HysteresisExit, so jitter doesn't switch the client's correction on
    /// and off.</item>
    /// </list>
    /// </remarks>
    public sealed class ClientMatchedRiftAlgorithm : IRiftAlgorithm
    {
        public string Name => "ClientMatched";

        public bool ShouldUpdate(uint serverFrame, ServerConfiguration config) => true;

        public void UpdateSmoothRift(PlayerInfo player, float riftError, ServerConfiguration config)
        {
            var s = config.RiftCalculation;
            float smooth = player.SmoothRift;

            if (MathF.Abs(riftError) < s.SmallErrorBelow && MathF.Abs(smooth) < s.SmallErrorBelow)
            {
                player.SmoothRift = s.SmallErrorAlpha * riftError + (1f - s.SmallErrorAlpha) * smooth;
            }
            else if (riftError * smooth < 0f)
            {
                player.SmoothRift = riftError;
            }
            else if (MathF.Abs(riftError) < 0.2f || MathF.Abs(riftError) < MathF.Abs(smooth))
            {
                player.SmoothRift = riftError;
            }
            else
            {
                float alpha = MathF.Min(s.RiftAlpha * 2.0f, 0.3f);
                player.SmoothRift = alpha * riftError + (1f - alpha) * smooth;
            }
        }

        public float Report(PlayerInfo player, ServerConfiguration config)
        {
            var s = config.RiftCalculation;
            float magnitude = MathF.Abs(player.SmoothRift);
            if (player.RiftCorrecting && magnitude < s.HysteresisExit)
                player.RiftCorrecting = false;
            else if (!player.RiftCorrecting && magnitude > s.HysteresisEnter)
                player.RiftCorrecting = true;
            return player.RiftCorrecting ? player.SmoothRift : 0f;
        }
    }
}
