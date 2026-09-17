using Windows.Devices.Geolocation;

namespace Llampec.Platform;

/// <summary>On-demand WinRT location. Call from the foreground UI in response to a user gesture.</summary>
public static class SystemLocation
{
    public readonly record struct Coordinates(double Latitude, double Longitude);

    public static async Task<Coordinates?> TryGetAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        var access = await Geolocator.RequestAccessAsync().AsTask(cancellationToken);
        if (access != GeolocationAccessStatus.Allowed) return null;
        var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
        try
        {
            var position = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(5), timeout).AsTask(cancellationToken);
            var point = position.Coordinate.Point.Position;
            return new Coordinates(point.Latitude, point.Longitude);
        }
        catch (UnauthorizedAccessException)
        {
            // Permission can be revoked between the request and the fix.
            return null;
        }
    }
}
