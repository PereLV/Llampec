namespace Llampec.Settings;

public enum ThemeScheduleMode { Off, Hours, Sun }

public sealed class ThemeScheduleSettings
{
    public ThemeScheduleMode Mode { get; set; }
    public TimeOnly LightAt { get; set; } = new(7, 0);
    public TimeOnly DarkAt { get; set; } = new(20, 0);
}

public readonly record struct ThemeSchedulePlan(bool Light, DateTimeOffset NextCheck, bool PolarDayOrNight = false)
{
    /// <summary>Mode at the next deadline, after resolving simultaneous transitions.</summary>
    public bool NextLight { get; init; } = !Light;
}

/// <summary>Pure, offline schedule calculation. All returned instants include their UTC offset.</summary>
public static class ThemeSchedule
{
    public static ThemeSchedulePlan? Calculate(ThemeScheduleSettings settings, DateTimeOffset now,
        TimeZoneInfo zone, double? latitude = null, double? longitude = null)
    {
        if (settings.Mode == ThemeScheduleMode.Off) return null;
        var events = new List<(DateTimeOffset At, bool Light)>();
        if (settings.Mode == ThemeScheduleMode.Hours)
        {
            if (settings.LightAt == settings.DarkAt) throw new ArgumentException("Light and dark times must differ.");
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
            for (int day = -2; day <= 3; day++)
            {
                events.Add((ResolveLocal(date.AddDays(day), settings.LightAt, zone), true));
                events.Add((ResolveLocal(date.AddDays(day), settings.DarkAt, zone), false));
            }
        }
        else if (settings.Mode == ThemeScheduleMode.Sun)
        {
            if (latitude is not double lat || longitude is not double lon || !double.IsFinite(lat)
                || !double.IsFinite(lon) || Math.Abs(lat) > 90 || Math.Abs(lon) > 180)
                throw new ArgumentException("Enter a latitude from -90 to 90 and a longitude from -180 to 180.");
            var date = DateOnly.FromDateTime(now.UtcDateTime);
            // Neighbouring UTC dates also cover locations across the date line.
            for (int day = -2; day <= 3; day++)
            {
                var solar = Solar(date.AddDays(day), lat, lon);
                if (solar.Rise is { } rise) events.Add((rise, true));
                if (solar.Set is { } set) events.Add((set, false));
            }
            if (!events.Any(e => e.At <= now) || !events.Any(e => e.At > now))
            {
                var solar = Solar(date, lat, lon, now.UtcDateTime.TimeOfDay.TotalMinutes);
                // No crossing during polar day/night: re-evaluate tomorrow, without polling.
                return new ThemeSchedulePlan(solar.PolarLight, new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero), true);
            }
        }
        else throw new ArgumentException("Unknown schedule mode.");
        // If DST moves two times to the same instant, the dark transition wins.
        var ordered = events.OrderBy(e => e.At).ThenByDescending(e => e.Light).ToArray();
        var next = ordered.First(e => e.At > now);
        return new ThemeSchedulePlan(ordered.Last(e => e.At <= now).Light, next.At)
        {
            NextLight = ordered.Last(e => e.At == next.At).Light,
        };
    }

    public static DateTimeOffset ResolveLocal(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        // Spring gap: first valid minute. Autumn overlap: first occurrence only.
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    private static (DateTimeOffset? Rise, DateTimeOffset? Set, bool PolarLight) Solar(DateOnly date, double latitude, double longitude, double utcMinutes = 720)
    {
        // NOAA fractional-year approximation; standard apparent horizon 90.833 degrees.
        // https://gml.noaa.gov/grad/solcalc/solareqns.PDF
        double g = 2 * Math.PI / (DateTime.IsLeapYear(date.Year) ? 366 : 365) * (date.DayOfYear - 1);
        double eq = 229.18 * (0.000075 + 0.001868 * Math.Cos(g) - 0.032077 * Math.Sin(g)
            - 0.014615 * Math.Cos(2 * g) - 0.040849 * Math.Sin(2 * g));
        double decl = 0.006918 - 0.399912 * Math.Cos(g) + 0.070257 * Math.Sin(g)
            - 0.006758 * Math.Cos(2 * g) + 0.000907 * Math.Sin(2 * g)
            - 0.002697 * Math.Cos(3 * g) + 0.00148 * Math.Sin(3 * g);
        double lat = latitude * Math.PI / 180;
        double cosH = (Math.Cos(90.833 * Math.PI / 180) - Math.Sin(lat) * Math.Sin(decl)) / (Math.Cos(lat) * Math.Cos(decl));
        if (cosH < -1 || cosH > 1) return (null, null, cosH < -1);
        double delta = 4 * Math.Acos(cosH) * 180 / Math.PI;
        var midnight = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        double noon = 720 - 4 * longitude - eq;
        double hourAngle = (utcMinutes + eq + 4 * longitude - 720) * Math.PI / 720;
        bool lightNow = Math.Sin(lat) * Math.Sin(decl) + Math.Cos(lat) * Math.Cos(decl) * Math.Cos(hourAngle)
            >= Math.Cos(90.833 * Math.PI / 180);
        return (midnight.AddMinutes(noon - delta), midnight.AddMinutes(noon + delta), lightNow);
    }
}
