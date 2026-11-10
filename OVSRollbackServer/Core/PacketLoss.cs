// PacketLoss.cs
using System;

namespace OVS.Rollback.Core
{
    /// <summary>
    /// A player's packet loss as the server can see it, for the PacketLossPercent of the PlayerInput it sends them
    /// (RiftCalculation.ReportPacketLoss). A game acknowledges every PlayerInput it receives, by its sequence number
    /// (PlayerInputAck: the same echo the ping is measured from), so of the PlayerInputs sent to a player, the share
    /// never acknowledged is the share lost on the way there or on the acknowledgement's way back. The ping phase's
    /// requests (answered by QualityData) count the same way.
    /// </summary>
    /// <remarks>
    /// Counted over the last <see cref="Seconds"/> closed seconds: a message acknowledged counts when its
    /// acknowledgement arrives, in whatever order (one overtaken by a later acknowledgement still settles its
    /// message), one still unacknowledged <see cref="TimeoutMs"/> after it was sent counts as lost (and stops being
    /// waited for: an acknowledgement later than that is not counted as either). With fewer than
    /// <see cref="MinimumMessages"/> messages settled in the window the previous figure stands.
    ///
    /// What the game does with the figure (final build; read 2026-11-10, the message pump 0x14125ec50): for a
    /// PlayerInput it stores short × 0.01 as its PacketLossPercent, so the wire value is hundredths of a percent
    /// (<see cref="Wire"/>), and while playing adds it to a last/min/max/mean accumulator (0x141245d80) beside the
    /// ping's. The stored value has no other use in the netcode's code (0x141100000 to 0x141300000), so the input
    /// delay and the rift correction there do not read it; who reads the accumulator was not traced. A spectating
    /// game skips the ping, the loss and the rift of every PlayerInput, so none is measured for a spectator.
    /// (RequestQualityData carries whole percent, and is sent before any PlayerInput: it stays 0.)
    /// </remarks>
    public sealed class PacketLossWindow
    {
        /// <summary>The seconds a figure is taken over.</summary>
        public const int Seconds = 5;
        /// <summary>The fewest settled messages a figure is made from.</summary>
        public const int MinimumMessages = 30;
        /// <summary>How long a PlayerInput is waited for before it counts as lost: well past any round trip measured (capped at 255 ms).</summary>
        public const long TimeoutMs = 1000;

        private readonly int[] _acked = new int[Seconds];
        private readonly int[] _lost = new int[Seconds];
        private int _current;

        /// <summary>The share lost over the window, in percent (0 to 100).</summary>
        public float Percent { get; private set; }

        /// <summary>A message sent to the player was acknowledged.</summary>
        public void Acked() => _acked[_current]++;

        /// <summary><paramref name="count"/> PlayerInputs timed out unacknowledged.</summary>
        public void Lost(int count) => _lost[_current] += count;

        /// <summary>Closes the current second: the figure over the window, then the oldest second makes way for the next.</summary>
        public float Roll()
        {
            int acked = 0, lost = 0;
            for (int i = 0; i < Seconds; i++)
            {
                acked += _acked[i];
                lost += _lost[i];
            }

            if (acked + lost >= MinimumMessages)
            {
                Percent = 100f * lost / (acked + lost);
            }

            _current = (_current + 1) % Seconds;
            _acked[_current] = 0;
            _lost[_current] = 0;
            return Percent;
        }

        /// <summary>A percentage as PlayerInput carries it: hundredths of a percent, 0 to 10000.</summary>
        public static short Wire(float percent) =>
            float.IsNaN(percent) ? (short)0 : (short)Math.Clamp((int)MathF.Round(percent * 100f), 0, 10000);
    }
}
