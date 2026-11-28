// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace NetMapLib;

public readonly record struct MapPoint(double X, double Y);

/// <summary>
/// Fits the offline map to its displayed size. All returned positions are in DIPs,
/// so the page can keep markers and labels readable at any window width.
/// </summary>
public sealed record MapViewport(double Width, double Height, double Scale, double CenterX, double OffsetX, double OffsetY)
{
    public const double WorldWidth = 960;
    public const double WorldHeight = 480;

    public static MapViewport Fit(IEnumerable<MapCoordinate?> coordinates, double width, double height, double zoom = 1)
    {
        width = double.IsFinite(width) ? Math.Max(1, width) : 960;
        height = double.IsFinite(height) ? Math.Max(1, height) : 480;
        zoom = double.IsFinite(zoom) ? Math.Clamp(zoom, 1, 8) : 1;
        var points = coordinates.Where(IsValid).Select(point => Project(point!)).ToArray();
        double centerX = WorldWidth / 2;
        double centerY = WorldHeight / 2;
        double scale = Math.Min(width / WorldWidth, height / WorldHeight);
        double targetY = height / 2;
        if (points.Length > 0)
        {
            var longitudes = points.Select(point => point.X).Distinct().Order().ToArray();
            double largestGap = -1;
            double left = 0;
            for (int i = 0; i < longitudes.Length; i++)
            {
                double next = i + 1 < longitudes.Length ? longitudes[i + 1] : longitudes[0] + WorldWidth;
                double gap = next - longitudes[i];
                if (gap > largestGap)
                {
                    largestGap = gap;
                    left = next % WorldWidth;
                }
            }

            double occupiedWidth = WorldWidth - largestGap;
            centerX = (left + (occupiedWidth / 2)) % WorldWidth;
            double top = points.Min(point => point.Y);
            double occupiedHeight = points.Max(point => point.Y) - top;
            centerY = top + (occupiedHeight / 2);

            // Reserve space for the zoom controls, bottom legend and marker radius.
            // A minimum regional span avoids an unusably close view for a single
            // location or multiple routers sharing the same approximate city point.
            double horizontalPadding = Math.Min(32, width / 4);
            double topPadding = Math.Min(54, height / 4);
            double bottomPadding = Math.Min(46, height / 4);
            double usableWidth = Math.Max(1, width - (horizontalPadding * 2));
            double usableHeight = Math.Max(1, height - topPadding - bottomPadding);
            targetY = topPadding + (usableHeight / 2);
            scale = Math.Min(usableWidth / Math.Max(occupiedWidth, 48), usableHeight / Math.Max(occupiedHeight, 32));
        }

        scale *= zoom;
        return new(width, height, scale, centerX, (width / 2) - (centerX * scale), targetY - (centerY * scale));
    }

    public MapPoint Position(MapCoordinate coordinate)
    {
        var point = Project(coordinate);
        double x = CenterX + (((point.X - CenterX + (WorldWidth * 1.5)) % WorldWidth) - (WorldWidth / 2));
        return new((x * Scale) + OffsetX, (point.Y * Scale) + OffsetY);
    }

    public MapViewport Pan(double deltaX, double deltaY) => double.IsFinite(deltaX) && double.IsFinite(deltaY)
        ? Normalize(Scale, OffsetX + deltaX, OffsetY + deltaY) : this;

    public MapViewport ZoomAt(double factor, MapPoint anchor) => double.IsFinite(factor) && factor > 0 && double.IsFinite(anchor.X) && double.IsFinite(anchor.Y)
        ? Normalize(Scale * factor, anchor.X - ((anchor.X - OffsetX) * factor), anchor.Y - ((anchor.Y - OffsetY) * factor)) : this;

    public MapViewport Resize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            return this;
        }

        var resized = this with { Width = width, Height = height };
        return resized.Normalize(Scale, OffsetX + ((width - Width) / 2), OffsetY + ((height - Height) / 2));
    }

    private MapViewport Normalize(double scale, double offsetX, double offsetY)
    {
        if (!double.IsFinite(scale) || scale <= 0 || !double.IsFinite(offsetX) || !double.IsFinite(offsetY))
        {
            return this;
        }

        // Keep the center in the canonical world copy so repeated date-line pans
        // never accumulate unbounded coordinates. Prevent empty space at the poles.
        double center = ((Width / 2) - offsetX) / scale;
        double wrapped = ((center % WorldWidth) + WorldWidth) % WorldWidth;
        double verticalSpace = Height - (WorldHeight * scale);
        return new(Width, Height, scale, wrapped, (Width / 2) - (wrapped * scale), verticalSpace >= 0 ? verticalSpace / 2 : Math.Clamp(offsetY, verticalSpace, 0));
    }

    public static MapPoint Project(MapCoordinate coordinate) => new((coordinate.Longitude + 180) * WorldWidth / 360, (90 - coordinate.Latitude) * WorldHeight / 180);

    private static bool IsValid(MapCoordinate? point) => point is { Longitude: >= -180 and <= 180, Latitude: >= -90 and <= 90 };
}
