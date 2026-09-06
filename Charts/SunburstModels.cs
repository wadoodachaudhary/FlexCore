namespace Fx.ControlKit.Charts;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

/// <summary>
/// Hierarchical node model for radial partitioned visualizations (Sunburst, Treemap).
/// </summary>
public class SunburstNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public double Value { get; set; }
    public double? SecondaryValue { get; set; }
    public string? Color { get; set; }
    public string? Description { get; set; }
    public SunburstNode? Parent { get; set; }
    public List<SunburstNode> Children { get; set; } = new();
    public Dictionary<string, object?> CustomAttributes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public SunburstNode() { }

    public SunburstNode(string name, double value, string? color = null)
    {
        Name = name;
        Value = value;
        Color = color;
    }

    public SunburstNode(string name, double value, double secondaryValue, string? color = null)
    {
        Name = name;
        Value = value;
        SecondaryValue = secondaryValue;
        Color = color;
    }

    public bool IsLeaf => Children.Count == 0;
    public int Depth => Parent == null ? 0 : Parent.Depth + 1;

    public double TotalValue =>
        Children.Count == 0
            ? Math.Max(0, Value)
            : (Value > 0 ? Value : Children.Sum(c => c.TotalValue));

    public double TotalSecondaryValue =>
        Children.Count == 0
            ? Math.Max(0, SecondaryValue ?? 0)
            : ((SecondaryValue ?? 0) > 0 ? SecondaryValue.GetValueOrDefault() : Children.Sum(c => c.TotalSecondaryValue));

    public void AddChild(SunburstNode child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    public IEnumerable<SunburstNode> GetAncestors()
    {
        var curr = Parent;
        var list = new List<SunburstNode>();
        while (curr != null)
        {
            list.Insert(0, curr);
            curr = curr.Parent;
        }
        return list;
    }

    public IEnumerable<SunburstNode> Flatten()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var descendant in child.Flatten())
            {
                yield return descendant;
            }
        }
    }
}

/// <summary>
/// Precomputed annular sector geometry for an individual node in the Sunburst partition.
/// </summary>
public class SunburstArc
{
    public SunburstNode Node { get; set; } = default!;
    public int Level { get; set; }
    public double InnerRadius { get; set; }
    public double OuterRadius { get; set; }
    public double StartAngle { get; set; }
    public double EndAngle { get; set; }
    public string FillColor { get; set; } = "#4a90e2";
    public string StrokeColor { get; set; } = "#ffffff";
    public double StrokeWidth { get; set; } = 0.75;

    public double AngularSpan => Math.Max(0, EndAngle - StartAngle);
    public double MidAngle => (StartAngle + EndAngle) / 2.0;
    public double MidRadius => (InnerRadius + OuterRadius) / 2.0;

    public bool IsVisible => AngularSpan > 0.0001;

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Generates the SVG path 'd' attribute for the annular sector.
    /// </summary>
    public string GenerateSvgPath(double cx, double cy)
    {
        var span = AngularSpan;
        if (InnerRadius <= 0)
        {
            if (span >= 2 * Math.PI - 0.001)
            {
                return $"M {F(cx - OuterRadius)} {F(cy)} " +
                       $"A {F(OuterRadius)} {F(OuterRadius)} 0 1 0 {F(cx + OuterRadius)} {F(cy)} " +
                       $"A {F(OuterRadius)} {F(OuterRadius)} 0 1 0 {F(cx - OuterRadius)} {F(cy)} Z";
            }

            var x1 = cx + OuterRadius * Math.Cos(StartAngle);
            var y1 = cy + OuterRadius * Math.Sin(StartAngle);
            var x2 = cx + OuterRadius * Math.Cos(EndAngle);
            var y2 = cy + OuterRadius * Math.Sin(EndAngle);
            var large = span > Math.PI ? 1 : 0;
            return $"M {F(cx)} {F(cy)} L {F(x1)} {F(y1)} A {F(OuterRadius)} {F(OuterRadius)} 0 {large} 1 {F(x2)} {F(y2)} Z";
        }
        else
        {
            if (span >= 2 * Math.PI - 0.001)
            {
                return $"M {F(cx - OuterRadius)} {F(cy)} " +
                       $"A {F(OuterRadius)} {F(OuterRadius)} 0 1 0 {F(cx + OuterRadius)} {F(cy)} " +
                       $"A {F(OuterRadius)} {F(OuterRadius)} 0 1 0 {F(cx - OuterRadius)} {F(cy)} " +
                       $"M {F(cx - InnerRadius)} {F(cy)} " +
                       $"A {F(InnerRadius)} {F(InnerRadius)} 0 1 1 {F(cx + InnerRadius)} {F(cy)} " +
                       $"A {F(InnerRadius)} {F(InnerRadius)} 0 1 1 {F(cx - InnerRadius)} {F(cy)} Z";
            }

            var xo1 = cx + OuterRadius * Math.Cos(StartAngle);
            var yo1 = cy + OuterRadius * Math.Sin(StartAngle);
            var xo2 = cx + OuterRadius * Math.Cos(EndAngle);
            var yo2 = cy + OuterRadius * Math.Sin(EndAngle);
            var xi2 = cx + InnerRadius * Math.Cos(EndAngle);
            var yi2 = cy + InnerRadius * Math.Sin(EndAngle);
            var xi1 = cx + InnerRadius * Math.Cos(StartAngle);
            var yi1 = cy + InnerRadius * Math.Sin(StartAngle);
            var large = span > Math.PI ? 1 : 0;

            return $"M {F(xo1)} {F(yo1)} " +
                   $"A {F(OuterRadius)} {F(OuterRadius)} 0 {large} 1 {F(xo2)} {F(yo2)} " +
                   $"L {F(xi2)} {F(yi2)} " +
                   $"A {F(InnerRadius)} {F(InnerRadius)} 0 {large} 0 {F(xi1)} {F(yi1)} Z";
        }
    }

    /// <summary>
    /// Computes centered anchor coordinates and angle for radial text rendering.
    /// </summary>
    public (double x, double y, double rotation, bool isFlipped) GetLabelTransform(double cx, double cy)
    {
        var x = cx + MidRadius * Math.Cos(MidAngle);
        var y = cy + MidRadius * Math.Sin(MidAngle);
        var deg = MidAngle * (180.0 / Math.PI);
        deg = (deg % 360 + 360) % 360;
        var flipped = deg > 90 && deg < 270;
        var finalRot = flipped ? deg + 180 : deg;
        return (x, y, finalRot, flipped);
    }
}

/// <summary>
/// Color shade generation utilities for creating hierarchical sunburst palettes.
/// </summary>
public static class SunburstColorHelper
{
    public static string AdjustLightness(string hex, double factor)
    {
        if (string.IsNullOrWhiteSpace(hex) || !hex.StartsWith("#") || (hex.Length != 7 && hex.Length != 4))
            return hex;

        try
        {
            int r, g, b;
            if (hex.Length == 7)
            {
                r = Convert.ToInt32(hex.Substring(1, 2), 16);
                g = Convert.ToInt32(hex.Substring(3, 2), 16);
                b = Convert.ToInt32(hex.Substring(5, 2), 16);
            }
            else
            {
                r = Convert.ToInt32(new string(hex[1], 2), 16);
                g = Convert.ToInt32(new string(hex[2], 2), 16);
                b = Convert.ToInt32(new string(hex[3], 2), 16);
            }

            if (factor < 1.0)
            {
                // Darken
                r = (int)Math.Clamp(r * factor, 0, 255);
                g = (int)Math.Clamp(g * factor, 0, 255);
                b = (int)Math.Clamp(b * factor, 0, 255);
            }
            else
            {
                // Lighten
                r = (int)Math.Clamp(r + (255 - r) * (factor - 1.0), 0, 255);
                g = (int)Math.Clamp(g + (255 - g) * (factor - 1.0), 0, 255);
                b = (int)Math.Clamp(b + (255 - b) * (factor - 1.0), 0, 255);
            }

            return $"#{r:X2}{g:X2}{b:X2}";
        }
        catch
        {
            return hex;
        }
    }

    public static string Darken(string hex, double percent) => AdjustLightness(hex, 1.0 - percent);
    public static string Lighten(string hex, double percent) => AdjustLightness(hex, 1.0 + percent);
}
