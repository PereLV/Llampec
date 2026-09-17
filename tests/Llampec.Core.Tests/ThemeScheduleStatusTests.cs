using Llampec.Settings;
using Xunit;

namespace Llampec.Tests;

public class ThemeScheduleStatusTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-17T12:00:00Z");

    [Theory]
    [InlineData(true, "en", "Light")]
    [InlineData(false, "en", "Dark")]
    [InlineData(true, "es", "Claro")]
    [InlineData(false, "es", "Oscuro")]
    [InlineData(true, "ca", "Clar")]
    [InlineData(false, "ca", "Fosc")]
    public void No_schedule_still_shows_the_actual_state(bool light, string language, string expected)
    {
        Assert.Equal(expected, ThemeScheduleStatus.Format(light, null, Now, language));
        Assert.Equal(expected, ThemeScheduleStatus.Format(light, null, Now, language, compact: true));
        Assert.Null(ThemeScheduleStatus.NextUpdate(null, Now));
    }

    [Theory]
    [InlineData(1, "1 min")]
    [InlineData(59, "1 min")]
    [InlineData(60, "1 min")]
    [InlineData(61, "2 min")]
    [InlineData(3599, "1 h")]
    [InlineData(3600, "1 h")]
    [InlineData(3601, "1 h 1 min")]
    [InlineData(7800, "2 h 10 min")]
    public void Countdown_rounds_up_and_stays_compact(int seconds, string duration)
    {
        var plan = new ThemeSchedulePlan(false, Now.AddSeconds(seconds));
        Assert.Equal("Oscuro\nClaro en " + duration, ThemeScheduleStatus.Format(false, plan, Now, "es"));
        Assert.Equal("Oscuro\n" + duration, ThemeScheduleStatus.Format(false, plan, Now, "es", compact: true));
    }

    [Theory]
    [InlineData("en", "Light\nDark in 2 h 10 min")]
    [InlineData("es", "Claro\nOscuro en 2 h 10 min")]
    [InlineData("ca", "Clar\nFosc en 2 h 10 min")]
    public void Scheduled_target_is_localized(string language, string expected)
    {
        var plan = new ThemeSchedulePlan(true, Now.AddMinutes(130));
        Assert.Equal(expected, ThemeScheduleStatus.Format(true, plan, Now, language));
    }

    [Theory]
    [InlineData("en", "Light\n4 h 27 min")]
    [InlineData("es", "Claro\n4 h 27 min")]
    [InlineData("ca", "Clar\n4 h 27 min")]
    public void Visible_tile_uses_two_compact_lines(string language, string expected)
    {
        var plan = new ThemeSchedulePlan(true, Now.AddMinutes(267));
        Assert.Equal(expected, ThemeScheduleStatus.Format(true, plan, Now, language, compact: true));
    }

    [Fact]
    public void Manual_override_does_not_report_the_planned_state_as_the_current_state()
    {
        // It is daytime in the schedule, but the user has manually switched to dark.
        var plan = new ThemeSchedulePlan(true, Now.AddHours(2));
        Assert.Equal("Dark\nDark in 2 h", ThemeScheduleStatus.Format(false, plan, Now, "en"));
    }

    [Fact]
    public void Simultaneous_spring_gap_transitions_report_the_winning_target()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
        var now = DateTimeOffset.Parse("2026-03-29T00:59:00Z");
        var plan = ThemeSchedule.Calculate(new()
        {
            Mode = ThemeScheduleMode.Hours,
            LightAt = new(2, 0),
            DarkAt = new(2, 30),
        }, now, zone)!.Value;

        Assert.False(plan.Light);
        Assert.False(plan.NextLight);
        Assert.Equal(now.AddMinutes(1), plan.NextCheck);
        Assert.Equal("Dark\nDark in 1 min", ThemeScheduleStatus.Format(false, plan, now, "en"));
        // A manual override may currently be light; the scheduled winner stays dark.
        Assert.Equal("Light\nDark in 1 min", ThemeScheduleStatus.Format(true, plan, now, "en"));
    }

    [Fact]
    public void Polar_daily_check_is_not_advertised_as_a_theme_change()
    {
        var plan = new ThemeSchedulePlan(true, Now.AddHours(12), PolarDayOrNight: true);
        Assert.Equal("Light", ThemeScheduleStatus.Format(true, plan, Now, "en"));
        Assert.Null(ThemeScheduleStatus.NextUpdate(plan, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public void Elapsed_deadline_never_displays_zero_or_negative_time(int seconds)
    {
        var plan = new ThemeSchedulePlan(true, Now.AddSeconds(seconds));
        Assert.Equal("Light", ThemeScheduleStatus.Format(true, plan, Now, "en"));
        Assert.Null(ThemeScheduleStatus.NextUpdate(plan, Now));
    }

    [Fact]
    public void Countdown_uses_instants_across_offsets_and_midnight()
    {
        var now = DateTimeOffset.Parse("2026-09-17T23:45:00+02:00");
        var plan = new ThemeSchedulePlan(false, DateTimeOffset.Parse("2026-09-18T00:15:00+03:00"));
        // The written local time is tomorrow, but its actual instant has already elapsed.
        Assert.Equal("Dark", ThemeScheduleStatus.Format(false, plan, now, "en"));
        plan = plan with { NextCheck = DateTimeOffset.Parse("2026-09-18T00:15:00+02:00") };
        Assert.Equal("Dark\nLight in 30 min", ThemeScheduleStatus.Format(false, plan, now, "en"));
    }

    [Fact]
    public void Visible_refresh_follows_solar_seconds_instead_of_polling_on_wall_minutes()
    {
        var plan = new ThemeSchedulePlan(true, Now.AddSeconds(125.25));
        Assert.Equal("Light\nDark in 3 min", ThemeScheduleStatus.Format(true, plan, Now, "en"));
        TimeSpan update = ThemeScheduleStatus.NextUpdate(plan, Now)!.Value;
        Assert.Equal(TimeSpan.FromSeconds(5.25), update);
        Assert.Equal("Light\nDark in 2 min", ThemeScheduleStatus.Format(true, plan, Now + update, "en"));
        Assert.Equal(TimeSpan.FromMinutes(1), ThemeScheduleStatus.NextUpdate(plan, Now + update));
    }

    [Fact]
    public void Last_minute_refreshes_at_the_transition_and_then_stops()
    {
        var plan = new ThemeSchedulePlan(false, Now.AddSeconds(27));
        Assert.Equal(TimeSpan.FromSeconds(27), ThemeScheduleStatus.NextUpdate(plan, Now));
        Assert.Null(ThemeScheduleStatus.NextUpdate(plan, plan.NextCheck));
    }
}
