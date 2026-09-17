using System.Globalization;

namespace Llampec.Settings;

/// <summary>Compact tile text and its next visible update, without reading or changing Windows.</summary>
public static class ThemeScheduleStatus
{
    public static string Format(bool isLight, ThemeSchedulePlan? plan, DateTimeOffset now, string language, bool compact = false)
    {
        string state = UiText.Translate(isLight ? "Light" : "Dark", language);
        if (Remaining(plan, now) is not { } remaining) return state;

        // Round up: a change in 30 seconds is still one minute away, never "0 min".
        int minutes = (int)Math.Ceiling(remaining.TotalMinutes);
        string duration = minutes < 60 ? Text("{0} min", minutes)
            : minutes % 60 == 0 ? Text("{0} h", minutes / 60)
            : Text("{0} h {1} min", minutes / 60, minutes % 60);
        return state + "\n" + (compact ? duration : Text(plan!.Value.NextLight ? "Light in {0}" : "Dark in {0}", duration));

        string Text(string key, params object[] values)
            => string.Format(CultureInfo.CurrentCulture, UiText.Translate(key, language), values);
    }

    /// <summary>Wake only when the rounded minute changes; no deadline means no display timer.</summary>
    public static TimeSpan? NextUpdate(ThemeSchedulePlan? plan, DateTimeOffset now)
    {
        if (Remaining(plan, now) is not { } remaining) return null;
        double minutes = Math.Ceiling(remaining.TotalMinutes);
        // Keep the display aligned with the scheduled instant, including solar seconds.
        return remaining - TimeSpan.FromMinutes(minutes - 1);
    }

    private static TimeSpan? Remaining(ThemeSchedulePlan? plan, DateTimeOffset now)
        // A polar-day check and an elapsed deadline are not future theme transitions.
        => plan is { PolarDayOrNight: false } next && next.NextCheck > now ? next.NextCheck - now : null;
}
