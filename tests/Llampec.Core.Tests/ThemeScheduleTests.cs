using Llampec.Settings;
using Xunit;

namespace Llampec.Tests;

public class ThemeScheduleTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    [Fact]
    public void Off_has_no_deadline() => Assert.Null(ThemeSchedule.Calculate(new(), DateTimeOffset.UtcNow, Utc));

    [Theory]
    [InlineData("2026-09-15T06:59:00Z", false, "2026-09-15T07:00:00Z")]
    [InlineData("2026-09-15T07:00:00Z", true, "2026-09-15T20:00:00Z")]
    [InlineData("2026-09-15T20:00:00Z", false, "2026-09-16T07:00:00Z")]
    [InlineData("2026-09-16T00:00:00Z", false, "2026-09-16T07:00:00Z")]
    public void Hours_handle_boundaries_and_midnight(string now, bool light, string next)
    {
        var plan = ThemeSchedule.Calculate(new() { Mode = ThemeScheduleMode.Hours }, DateTimeOffset.Parse(now), Utc)!.Value;
        Assert.Equal(light, plan.Light);
        Assert.Equal(!light, plan.NextLight);
        Assert.Equal(DateTimeOffset.Parse(next), plan.NextCheck);
    }

    [Fact]
    public void Light_interval_can_cross_midnight()
    {
        var plan = ThemeSchedule.Calculate(new() { Mode = ThemeScheduleMode.Hours, LightAt = new(20, 0), DarkAt = new(7, 0) },
            DateTimeOffset.Parse("2026-09-15T23:00:00Z"), Utc)!.Value;
        Assert.True(plan.Light);
        Assert.False(plan.NextLight);
        Assert.Equal(DateTimeOffset.Parse("2026-09-16T07:00:00Z"), plan.NextCheck);
    }

    [Fact]
    public void Equal_hours_are_rejected() => Assert.Throws<ArgumentException>(() => ThemeSchedule.Calculate(
        new() { Mode = ThemeScheduleMode.Hours, LightAt = new(7, 0), DarkAt = new(7, 0) }, DateTimeOffset.UtcNow, Utc));

    [Fact]
    public void Dst_gap_uses_first_valid_time_and_overlap_first_occurrence()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
        Assert.Equal(DateTimeOffset.Parse("2026-03-29T01:00:00Z"), ThemeSchedule.ResolveLocal(new(2026, 3, 29), new(2, 30), zone));
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T00:30:00Z"), ThemeSchedule.ResolveLocal(new(2026, 10, 25), new(2, 30), zone));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(91.0, 0.0)]
    [InlineData(0.0, 181.0)]
    [InlineData(double.NaN, 0.0)]
    public void Sun_requires_valid_coordinates(double? lat, double? lon) => Assert.Throws<ArgumentException>(() =>
        ThemeSchedule.Calculate(new() { Mode = ThemeScheduleMode.Sun }, DateTimeOffset.UtcNow, Utc, lat, lon));

    [Fact]
    public void Greenwich_equinox_has_sunset_near_18_utc()
    {
        var plan = ThemeSchedule.Calculate(new() { Mode = ThemeScheduleMode.Sun }, DateTimeOffset.Parse("2026-03-20T12:00:00Z"), Utc, 51.48, 0)!.Value;
        Assert.True(plan.Light);
        Assert.InRange(plan.NextCheck.UtcDateTime.TimeOfDay.TotalMinutes, 18 * 60, 18 * 60 + 25);
    }

    [Theory]
    [InlineData("2026-06-21T12:00:00Z", true)]
    [InlineData("2026-12-21T12:00:00Z", false)]
    public void Polar_day_and_night_retry_tomorrow(string now, bool light)
    {
        var instant = DateTimeOffset.Parse(now);
        var plan = ThemeSchedule.Calculate(new() { Mode = ThemeScheduleMode.Sun }, instant, Utc, 89, 0)!.Value;
        Assert.Equal(light, plan.Light);
        Assert.True(plan.PolarDayOrNight);
        Assert.InRange(plan.NextCheck - instant, TimeSpan.FromSeconds(1), TimeSpan.FromDays(1));
    }

    [Theory]
    [InlineData(179.9)]
    [InlineData(-179.9)]
    public void Date_line_returns_future_transition(double longitude)
    {
        var now = DateTimeOffset.Parse("2026-09-15T23:00:00Z");
        var plan = ThemeSchedule.Calculate(new() { Mode = ThemeScheduleMode.Sun }, now, Utc, 0, longitude)!.Value;
        Assert.InRange(plan.NextCheck - now, TimeSpan.FromSeconds(1), TimeSpan.FromHours(13));
    }
}
