using System.Text.Json;
using OVS.Rollback.Common;
using OVS.Rollback.Models;
using Xunit;

namespace OVS.Rollback.P2P.Tests;

/// <summary>
/// A match's own frame rate (tick_rate, the Beta Speed mutator's 72): read from the match config as the HTTP server
/// sends it, used only when sane, else the server's rate.
/// </summary>
public class TickRateTests
{
    private const float Server60 = 1000f / 60f;

    [Fact]
    public void TheMatchConfigCarriesTickRate()
    {
        var config = JsonSerializer.Deserialize("""{"max_players":2,"match_duration":36000,"tick_rate":72,"players":[]}""", OVSJsonContext.Default.OVSMatchConfig)!;
        Assert.Equal(72, config.TickRate);
        Assert.Equal(1000f / 72f, config.FrameTimeMs(Server60), 3);
    }

    [Fact]
    public void WithoutTickRateTheServersRateHolds()
    {
        var config = JsonSerializer.Deserialize("""{"max_players":2,"players":[]}""", OVSJsonContext.Default.OVSMatchConfig)!;
        Assert.Null(config.TickRate);
        Assert.Equal(Server60, config.FrameTimeMs(Server60));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(29)]
    [InlineData(241)]
    [InlineData(-72)]
    public void AnInsaneTickRateIsIgnored(int rate) =>
        Assert.Equal(Server60, new OVSMatchConfig { TickRate = rate }.FrameTimeMs(Server60));
}
