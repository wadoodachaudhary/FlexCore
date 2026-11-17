using System.Globalization;

namespace Fx.ControlKit.Maps;

/// <summary>Fitted map projection. The slippy map keeps using <see cref="MapProjection"/>.</summary>
public enum MapProjectionKind { Robinson, Albers, Mercator }

/// <summary>Longitude/latitude window. West and south are inclusive.</summary>
public sealed class MapExtent
{
    public double West { get; set; }
    public double South { get; set; }
    public double East { get; set; }
    public double North { get; set; }
}

/// <summary>Projects geography into a view box. Y grows downward, matching SVG.</summary>
public sealed class MapFrame
{
    public MapProjectionKind Kind { get; private init; }
    public double Width { get; private init; }
    public double Height { get; private init; }
    double _lambda0, _phi0, _phi1, _phi2, _minX, _maxX, _minY, _maxY, _scale, _padLeft, _padTop, _boxW, _boxH, _dx, _dy;

    public MapPixel Project(GeoPoint point)
    {
        var plane = Plane(point);
        var contentW = (_maxX - _minX) * _scale;
        var contentH = (_maxY - _minY) * _scale;
        var x = _padLeft + (_boxW - contentW) / 2 + (plane.X - _minX) * _scale;
        var y = _padTop + (_boxH - contentH) / 2 + (_maxY - plane.Y) * _scale;
        return new(x + _dx, y + _dy);
    }

    public MapFrame Translated(double dx, double dy)
    {
        var copy = (MapFrame)MemberwiseClone();
        copy._dx += dx;
        copy._dy += dy;
        return copy;
    }

    public static MapFrame Fit(IEnumerable<GeoPoint> points, MapProjectionKind kind, double width, double height, double padLeft, double padTop, double padRight, double padBottom)
    {
        var list = points.Where(p => double.IsFinite(p.Latitude) && double.IsFinite(p.Longitude)).ToList();
        if (list.Count == 0) throw new ArgumentException("A map frame needs at least one coordinate.");
        if (width < 32 || height < 32) throw new ArgumentException("Map frame is too small.");
        var west = list.Min(p => p.Longitude);
        var east = list.Max(p => p.Longitude);
        var south = Math.Clamp(list.Min(p => p.Latitude), -85, 85);
        var north = Math.Clamp(list.Max(p => p.Latitude), -85, 85);
        if (north < south) (south, north) = (north, south);
        var frame = new MapFrame
        {
            Kind = kind,
            Width = width,
            Height = height,
            _lambda0 = (west + east) / 2,
            _phi0 = (south + north) / 2,
            _phi1 = south + (north - south) / 6,
            _phi2 = north - (north - south) / 6,
            _padLeft = padLeft,
            _padTop = padTop,
            _boxW = Math.Max(8, width - padLeft - padRight),
            _boxH = Math.Max(8, height - padTop - padBottom)
        };
        var projected = list.Select(frame.Plane).ToList();
        frame._minX = projected.Min(p => p.X);
        frame._maxX = projected.Max(p => p.X);
        frame._minY = projected.Min(p => p.Y);
        frame._maxY = projected.Max(p => p.Y);
        var spanX = Math.Max(frame._maxX - frame._minX, 1e-9);
        var spanY = Math.Max(frame._maxY - frame._minY, 1e-9);
        frame._scale = Math.Min(frame._boxW / spanX, frame._boxH / spanY);
        return frame;
    }

    (double X, double Y) Plane(GeoPoint point) => Kind switch
    {
        MapProjectionKind.Robinson => Robinson(point),
        MapProjectionKind.Mercator => Mercator(point, _lambda0),
        _ => Albers(point, _lambda0, _phi0, _phi1, _phi2)
    };

    // Robinson lookup from Snyder, rounded the way Natural Earth publishes it.
    static readonly double[] RobinsonX = [1, 0.9986, 0.9954, 0.9900, 0.9822, 0.9730, 0.9600, 0.9427, 0.9216, 0.8962, 0.8679, 0.8350, 0.7986, 0.7597, 0.7186, 0.6732, 0.6213, 0.5722, 0.5322];
    static readonly double[] RobinsonY = [0, 0.0620, 0.1240, 0.1860, 0.2480, 0.3100, 0.3720, 0.4340, 0.4958, 0.5571, 0.6176, 0.6769, 0.7346, 0.7903, 0.8435, 0.8936, 0.9394, 0.9761, 1];

    public static (double X, double Y) Robinson(GeoPoint point)
    {
        var lat = Math.Clamp(Math.Abs(point.Latitude), 0, 90);
        var bucket = Math.Min((int)(lat / 5), 17);
        var t = (lat - bucket * 5) / 5;
        var x = RobinsonX[bucket] + (RobinsonX[bucket + 1] - RobinsonX[bucket]) * t;
        var y = RobinsonY[bucket] + (RobinsonY[bucket + 1] - RobinsonY[bucket]) * t;
        return (0.8487 * x * point.Longitude * Math.PI / 180, 1.3523 * y * Math.Sign(point.Latitude == 0 ? 1 : point.Latitude));
    }

    public static (double X, double Y) Albers(GeoPoint point, double lambda0, double phi0, double phi1, double phi2)
    {
        var p0 = Rad(phi0);
        var p1 = Rad(phi1);
        var p2 = Rad(phi2);
        var n = (Math.Sin(p1) + Math.Sin(p2)) / 2;
        if (Math.Abs(n) < 1e-8) n = Math.Abs(Math.Sin(p0)) < 1e-8 ? 1e-6 : Math.Sin(p0);
        var c = Math.Cos(p1) * Math.Cos(p1) + 2 * n * Math.Sin(p1);
        var phi = Rad(Math.Clamp(point.Latitude, -89, 89));
        var rho0 = Math.Sqrt(Math.Max(0, c - 2 * n * Math.Sin(p0))) / n;
        var rho = Math.Sqrt(Math.Max(0, c - 2 * n * Math.Sin(phi))) / n;
        var theta = n * (Rad(point.Longitude) - Rad(lambda0));
        return (rho * Math.Sin(theta), rho0 - rho * Math.Cos(theta));
    }

    public static (double X, double Y) Mercator(GeoPoint point, double lambda0)
    {
        var lat = Rad(Math.Clamp(point.Latitude, -85, 85));
        return (Rad(point.Longitude - lambda0), Math.Log(Math.Tan(Math.PI / 4 + lat / 2)));
    }

    static double Rad(double degrees) => degrees * Math.PI / 180;

    public static List<GeoPoint> ClipRing(IReadOnlyList<GeoPoint> ring, MapExtent extent)
    {
        var output = ring.ToList();
        if (output.Count > 1 && Same(output[0], output[^1])) output.RemoveAt(output.Count - 1);
        output = Clip(output, p => p.Longitude >= extent.West, (a, b) => new(Lerp(a.Latitude, b.Latitude, a.Longitude, b.Longitude, extent.West), extent.West));
        output = Clip(output, p => p.Longitude <= extent.East, (a, b) => new(Lerp(a.Latitude, b.Latitude, a.Longitude, b.Longitude, extent.East), extent.East));
        output = Clip(output, p => p.Latitude >= extent.South, (a, b) => new(extent.South, Lerp(a.Longitude, b.Longitude, a.Latitude, b.Latitude, extent.South)));
        output = Clip(output, p => p.Latitude <= extent.North, (a, b) => new(extent.North, Lerp(a.Longitude, b.Longitude, a.Latitude, b.Latitude, extent.North)));
        if (output.Count < 3) return [];
        output.Add(output[0]);
        return output;
    }

    static List<GeoPoint> Clip(List<GeoPoint> input, Func<GeoPoint, bool> inside, Func<GeoPoint, GeoPoint, GeoPoint> cross)
    {
        if (input.Count == 0) return input;
        var output = new List<GeoPoint>();
        var previous = input[^1];
        var previousIn = inside(previous);
        foreach (var point in input)
        {
            var pointIn = inside(point);
            if (pointIn)
            {
                if (!previousIn) output.Add(cross(previous, point));
                output.Add(point);
            }
            else if (previousIn) output.Add(cross(previous, point));
            previous = point;
            previousIn = pointIn;
        }
        return output;
    }

    static double Lerp(double a, double b, double from, double to, double at)
    {
        var span = to - from;
        if (Math.Abs(span) < 1e-12) return a;
        return a + (b - a) * ((at - from) / span);
    }

    static bool Same(GeoPoint a, GeoPoint b) => Math.Abs(a.Latitude - b.Latitude) < 1e-9 && Math.Abs(a.Longitude - b.Longitude) < 1e-9;

    public static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
