// PingParityTests.cs
using System.Linq;
using OVS.Rollback.Configuration;
using OVS.Rollback.Core;
using Xunit;

namespace OVS.Rollback.P2P.Tests;

/// <summary>
/// The match-wide ping told under RiftCalculation.HostPingParity (Core/PingParity). The inputs are what Tick
/// accumulates over the remote active players (spectators, bots, the host's own game and players without a round
/// trip are never in them); these tests pin what is made of those inputs.
/// </summary>
public class PingParityTests
{
    private static RiftCalculationSettings Settings(string? aggregate = null, float? scale = null)
    {
        var s = new RiftCalculationSettings();
        if (aggregate is not null) s.PingParityAggregate = aggregate;
        if (scale is not null) s.PingParityScale = scale.Value;
        return s;
    }

    [Fact]
    public void The_defaults_are_the_behaviour_before_the_settings_existed()
    {
        var s = new RiftCalculationSettings();
        Assert.False(s.HostPingParity);
        Assert.Equal("Slowest", s.PingParityAggregate);
        Assert.Equal(1.0f, s.PingParityScale);
        Assert.False(s.PingParityEveryone);
        // Three remote players at 30, 30 and 150 ms: the slowest, unscaled.
        Assert.Equal(150, PingParity.Told(150f, 210f, 3, s));
    }

    [Fact]
    public void Mean_averages_the_remote_round_trips_and_the_name_is_case_insensitive()
    {
        Assert.Equal(70, PingParity.Told(150f, 210f, 3, Settings(aggregate: "Mean")));
        Assert.Equal(70, PingParity.Told(150f, 210f, 3, Settings(aggregate: "mean")));
        Assert.Equal(150, PingParity.Told(150f, 210f, 3, Settings(aggregate: "slowest")));
    }

    [Fact]
    public void An_unknown_aggregate_means_slowest()
    {
        Assert.False(PingParity.TryGetAggregate("Median", out string aggregate));
        Assert.Equal(PingParity.Slowest, aggregate);
        Assert.False(PingParity.TryGetAggregate(null, out _));
        Assert.True(PingParity.TryGetAggregate("MEAN", out aggregate));
        Assert.Equal(PingParity.Mean, aggregate);
        Assert.Equal(150, PingParity.Told(150f, 210f, 3, Settings(aggregate: "Median")));
    }

    [Fact]
    public void The_scale_multiplies_the_number_told_so_half_is_what_a_midway_relay_would_have_told_each_player()
    {
        // One guest at a 160 ms round trip: told 80, the game's curve gives input delay 3 instead of 6.
        Assert.Equal(80, PingParity.Told(160f, 160f, 1, Settings(scale: 0.5f)));
        Assert.Equal(35, PingParity.Told(150f, 210f, 3, Settings(aggregate: "Mean", scale: 0.5f)));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-0.5f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void A_scale_that_is_not_a_positive_number_means_one(float configured)
    {
        Assert.Equal(1f, PingParity.Scale(configured));
        Assert.Equal(160, PingParity.Told(160f, 160f, 1, Settings(scale: configured)));
    }

    [Fact]
    public void No_remote_round_trip_yet_means_no_override()
    {
        Assert.Equal(-1, PingParity.Told(0f, 0f, 0, Settings()));
        Assert.Equal(-1, PingParity.Told(0f, 0f, 0, Settings(aggregate: "Mean", scale: 0.5f)));
    }

    // ── The source: the median of a player's recent raw round trips ──

    private static (short[] Ring, int Count, int Next) Ring(params short[] samples)
    {
        var ring = new short[256];
        int next = 0, count = 0;
        foreach (short s in samples)
        {
            ring[next] = s;
            next = (next + 1) % ring.Length;
            count = Math.Min(count + 1, ring.Length);
        }
        return (ring, count, next);
    }

    [Fact]
    public void The_median_is_the_middle_sample_and_the_lower_one_of_an_even_count()
    {
        var (ring, count, next) = Ring(90, 80, 100);
        Assert.Equal(90, PingParity.MedianOf(ring, count, next, 256));
        (ring, count, next) = Ring(80, 90, 100, 110);
        Assert.Equal(90, PingParity.MedianOf(ring, count, next, 256));
        Assert.Equal(0, PingParity.MedianOf(new short[256], 0, 0, 256));
    }

    [Fact]
    public void The_median_reads_only_the_newest_window_and_follows_the_ring_round()
    {
        var (ring, count, next) = Ring(10, 10, 10, 10, 200, 200, 200);
        Assert.Equal(200, PingParity.MedianOf(ring, count, next, 3));
        Assert.Equal(10, PingParity.MedianOf(ring, count, next, 7));
        // 300 samples into a 256-ring: the newest 256 are 44..299, and the newest 5 are 295..299.
        var many = Enumerable.Range(0, 300).Select(i => (short)Math.Min(i, 255)).ToArray();
        (ring, count, next) = Ring(many);
        Assert.Equal(255, PingParity.MedianOf(ring, count, next, 5));
        Assert.Equal(256, count);
    }

    [Fact]
    public void A_minority_of_spikes_does_not_move_the_median_where_it_moves_the_average()
    {
        var samples = Enumerable.Repeat((short)160, 55).Concat(Enumerable.Repeat((short)255, 5)).ToArray();
        var (ring, count, next) = Ring(samples);
        Assert.Equal(160, PingParity.MedianOf(ring, count, next, 60));
        float average = 160f;
        foreach (short s in samples) average = 0.12f * s + 0.88f * average;
        Assert.True(average > 180f, $"the exponential average moved to {average:F0}");
    }

    [Fact]
    public void The_window_is_match_time_converted_at_one_sample_per_tick()
    {
        Assert.Equal(60, PingParity.WindowSamples(1000, 1000f / 60f));
        Assert.Equal(66, PingParity.WindowSamples(1000, 1000f / 66f));
        Assert.Equal(72, PingParity.WindowSamples(1000, 1000f / 72f));
        Assert.Equal(256, PingParity.WindowSamples(10_000, 1000f / 60f));
        Assert.Equal(1, PingParity.WindowSamples(0, 1000f / 60f));
        Assert.Equal(1, PingParity.WindowSamples(1000, 0f));
    }

    [Fact]
    public void An_unknown_source_means_smoothed()
    {
        Assert.False(PingParity.TryGetSource("Mean", out string source));
        Assert.Equal(PingParity.Smoothed, source);
        Assert.True(PingParity.TryGetSource("median", out source));
        Assert.Equal(PingParity.Median, source);
    }

    // ── Tick-rate compensation: the same milliseconds of cover at any tick rate ──

    [Theory]
    [InlineData(60, 160)]
    [InlineData(66, 176)]
    [InlineData(72, 192)]
    public void Compensation_scales_the_told_value_by_sixty_hertz_over_the_match_rate(int tickRate, short told)
    {
        var s = Settings();
        s.PingParityTickRateCompensation = true;
        Assert.Equal(told, PingParity.Told(160f, 160f, 1, s, 1000f / tickRate));
        // Off: the measured value at every rate.
        Assert.Equal(160, PingParity.Told(160f, 160f, 1, Settings(), 1000f / tickRate));
    }

    [Fact]
    public void Compensation_composes_with_the_scale_and_ignores_an_unusable_interval()
    {
        var s = Settings(scale: 0.5f);
        s.PingParityTickRateCompensation = true;
        Assert.Equal(96, PingParity.Told(160f, 160f, 1, s, 1000f / 72f));
        Assert.Equal(80, PingParity.Told(160f, 160f, 1, s, 0f));
        Assert.Equal(1f, PingParity.Compensation(float.NaN));
    }

    [Fact]
    public void The_number_told_never_exceeds_the_wire_limit()
    {
        Assert.Equal(255, PingParity.Told(600f, 600f, 1, Settings()));
        Assert.Equal(255, PingParity.Told(200f, 200f, 1, Settings(scale: 2f)));
    }
}
