// purpose: 8-point compass direction from one world position to another on the horizontal plane
// owns:    Bearing.Format, Bearing.None, Bearing.MinHorizontalDistance
// deps:    WorldPosition
// caution: axis convention is Unity's: +Z is North, +X is East, Y (height) is ignored

using System;

using ShelfTransfer.Core.Gateway;

namespace ShelfTransfer.Core.Shelves;

public static class Bearing
{
    /// <summary>Returned when the direction is undefined: positions coincide horizontally or a coordinate is not finite.</summary>
    public const string None = "";

    /// <summary>Below the 0.1 m key resolution, so two distinct shelf keys always get a direction.</summary>
    public const double MinHorizontalDistance = 0.01;

    private static readonly string[] _points = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    /// <summary>Sectors are 45° wide and centred on each point; a bearing exactly on a boundary goes to the clockwise point.</summary>
    public static string Format(WorldPosition from, WorldPosition to)
    {
        double east = (double)to.X - from.X;
        double north = (double)to.Z - from.Z;
        if (!double.IsFinite(east) || !double.IsFinite(north) || Math.Sqrt((east * east) + (north * north)) < MinHorizontalDistance)
        {
            return None;
        }

        double degrees = Math.Atan2(east, north) * 180.0 / Math.PI;
        if (degrees < 0)
        {
            degrees += 360.0;
        }

        int sector = (int)Math.Floor((degrees + 22.5) / 45.0) % _points.Length;
        return _points[sector];
    }
}
