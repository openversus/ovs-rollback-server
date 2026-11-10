// PacketLossTests.cs
using OVS.Rollback.Configuration;
using OVS.Rollback.Core;
using Xunit;

namespace OVS.Rollback.P2P.Tests;

/// <summary>
/// The packet loss told to a player (Core/PacketLoss): the share of the PlayerInputs sent to them that were never
/// acknowledged, over the last seconds, as the wire carries it. The inputs are what the engine counts (an
/// acknowledgement when it arrives, a timeout once a second); these tests pin what is made of them.
/// </summary>
public class PacketLossTests
{
    private static float Second(PacketLossWindow window, int acked, int lost)
    {
        for (int i = 0; i < acked; i++)
        {
            window.Acked();
        }

        window.Lost(lost);
        return window.Roll();
    }

    [Fact]
    public void It_is_reported_unless_switched_off() => Assert.True(new RiftCalculationSettings().ReportPacketLoss);

    [Fact]
    public void Nothing_lost_is_zero_and_the_share_lost_is_its_percentage()
    {
        var window = new PacketLossWindow();
        Assert.Equal(0f, Second(window, acked: 60, lost: 0));
        // 60 + 54 acknowledged, 6 lost: 5 %.
        Assert.Equal(5f, Second(window, acked: 54, lost: 6), 3);
        Assert.Equal(5f, window.Percent, 3);
    }

    [Fact]
    public void It_is_taken_over_the_last_five_seconds()
    {
        var window = new PacketLossWindow();
        Assert.Equal(50f, Second(window, acked: 30, lost: 30), 3);
        // Four clean seconds dilute it, the fifth pushes the bad second out of the window.
        float percent = 0f;
        for (int i = 0; i < 4; i++)
        {
            percent = Second(window, acked: 60, lost: 0);
        }

        Assert.Equal(10f, percent, 3);
        Assert.Equal(0f, Second(window, acked: 60, lost: 0));
    }

    [Fact]
    public void Too_few_messages_leave_the_previous_figure()
    {
        var window = new PacketLossWindow();
        // The first second of a match: a handful of messages settled, one lost. Not a figure yet.
        Assert.Equal(0f, Second(window, acked: 4, lost: 1));
        Assert.Equal(10f, Second(window, acked: 50, lost: 5), 3);
        // A player who stops being sent anything: the window empties second by second (the first second's 5 leave,
        // then 55 are left: 5 of 55), and once too few are left the last figure stands.
        for (int i = 0; i < PacketLossWindow.Seconds; i++)
        {
            Second(window, acked: 0, lost: 0);
        }

        Assert.Equal(100f * 5 / 55, window.Percent, 3);
    }

    [Theory]
    // PlayerInput carries hundredths of a percent (the game multiplies by 0.01).
    [InlineData(0f, 0)]
    [InlineData(5f, 500)]
    [InlineData(0.004f, 0)]
    [InlineData(0.006f, 1)]
    [InlineData(12.34f, 1234)]
    [InlineData(100f, 10000)]
    [InlineData(250f, 10000)]
    [InlineData(-3f, 0)]
    [InlineData(float.NaN, 0)]
    public void The_wire_value_is_hundredths_of_a_percent(float percent, short wire) => Assert.Equal(wire, PacketLossWindow.Wire(percent));
}
