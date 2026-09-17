using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

public sealed record ReportVectorText(string Value, string FontFamily, double FontSize, int Weight = 400, bool Italic = false,
    bool Underline = false, bool StrikeOut = false, string Anchor = "start", string Baseline = "alphabetic", double[]? Advances = null, double[]? VerticalAdvances = null);
public sealed record ReportVectorShape(string Kind, double[] Points, string Fill, string Stroke, double StrokeWidth, bool Winding = false,
    double[]? Clip = null, double[]? Transform = null, ReportVectorText? Text = null, double[][]? ClipPolygons = null);

/// <summary>A bounded geometry/text vocabulary, never imported SVG, markup or external resources.</summary>
public sealed class ReportVectorImage
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public List<ReportVectorShape> Shapes { get; set; } = [];
    public ReportVectorImage Clone() => new() { Left = Left, Top = Top, Width = Width, Height = Height,
        Shapes = Shapes.Select(s => s with { Points = s.Points.ToArray(), Clip = s.Clip?.ToArray(), Transform = s.Transform?.ToArray(),
            ClipPolygons = s.ClipPolygons?.Select(p => p.ToArray()).ToArray(),
            Text = s.Text is { } text ? text with { Advances = text.Advances?.ToArray(), VerticalAdvances = text.VerticalAdvances?.ToArray() } : null }).ToList() };

    public void Validate()
    {
        static bool Number(double n) => double.IsFinite(n) && Math.Abs(n) <= 100_000_000;
        static bool Color(string color) => color == "none" || color.Length == 7 && color[0] == '#' && color[1..].All(Uri.IsHexDigit);
        if (!Number(Left) || !Number(Top) || !Number(Width) || !Number(Height) || Width <= 0 || Height <= 0 || Shapes.Count is 0 or > 10000)
            throw new InvalidDataException("Invalid vector frame or shape count.");
        var count = 0;
        foreach (var shape in Shapes)
        {
            if (shape.Kind is not ("Rectangle" or "Ellipse" or "Polygon" or "Polyline" or "Bezier" or "Text") || shape.Points.Length < (shape.Kind == "Text" ? 2 : 4) || shape.Points.Length % 2 != 0
                || shape.Points.Any(n => !Number(n)) || (count += shape.Points.Length) > 200000
                || shape.Kind is "Rectangle" or "Ellipse" && (shape.Points.Length != 4 || shape.Points[2] < 0 || shape.Points[3] < 0)
                || shape.Kind == "Bezier" && (shape.Points.Length < 8 || (shape.Points.Length - 2) % 6 != 0)
                || !Color(shape.Fill) || !Color(shape.Stroke) || !Number(shape.StrokeWidth) || shape.StrokeWidth < 0
                || shape.Clip is { } clip && (clip.Length != 4 || clip.Any(n => !Number(n)) || clip[2] < 0 || clip[3] < 0)
                || shape.Transform is { } matrix && (matrix.Length != 6 || matrix.Any(n => !Number(n)) || Math.Abs(matrix[0] * matrix[3] - matrix[1] * matrix[2]) < 1e-12)
                || (shape.Kind == "Text") != (shape.Text is not null))
                throw new InvalidDataException("Invalid or unsupported vector geometry.");
            if (shape.ClipPolygons is { } clips && (clips.Length > 64 || clips.Any(p => p.Length is < 6 or > 128 || p.Length % 2 != 0
                || p.Any(n => !Number(n)) || (count += p.Length) > 200000)))
                throw new InvalidDataException("Invalid or excessive vector clip geometry.");
            if (shape.Text is { } text)
            {
                if (shape.Points.Length != 2 || text.Value.Length > 16000 || (count += text.Value.Length) > 200000
                    || text.FontFamily.Length is 0 or > 128 || text.FontFamily.Any(c => !char.IsLetterOrDigit(c) && c is not (' ' or '-' or '_'))
                    || !Number(text.FontSize) || text.FontSize <= 0 || text.Weight is < 1 or > 1000
                    || text.Anchor is not ("start" or "middle" or "end") || text.Baseline is not ("alphabetic" or "text-before-edge" or "text-after-edge")
                    || text.Advances is { } dx && (dx.Length != text.Value.Length || dx.Any(n => !Number(n)) || text.Value.Any(char.IsSurrogate))
                    || text.VerticalAdvances is { } dy && (text.Advances is null || dy.Length != text.Value.Length || dy.Any(n => !Number(n))))
                    throw new InvalidDataException("Invalid or unsupported vector text.");
                try { System.Xml.XmlConvert.VerifyXmlChars(text.Value); }
                catch (System.Xml.XmlException error) { throw new InvalidDataException("Invalid vector text encoding.", error); }
            }
        }
    }

    public XElement ToXml()
    {
        Validate();
        return new("Vector", new XAttribute("Left", N(Left)), new XAttribute("Top", N(Top)), new XAttribute("Width", N(Width)), new XAttribute("Height", N(Height)),
            Shapes.Select(s => new XElement("Shape", new XAttribute("Kind", s.Kind), new XAttribute("Points", string.Join(" ", s.Points.Select(N))),
                new XAttribute("Fill", s.Fill), new XAttribute("Stroke", s.Stroke), new XAttribute("StrokeWidth", N(s.StrokeWidth)), new XAttribute("Winding", s.Winding),
                s.Clip is null ? null : new XAttribute("Clip", string.Join(" ", s.Clip.Select(N))),
                s.Transform is null ? null : new XAttribute("Transform", string.Join(" ", s.Transform.Select(N))),
                s.ClipPolygons?.Select(p => new XElement("ClipPolygon", new XAttribute("Points", string.Join(" ", p.Select(N))))),
                s.Text is not { } t ? null : new XElement("Text", new XAttribute("Font", t.FontFamily), new XAttribute("Size", N(t.FontSize)),
                    new XAttribute("Weight", t.Weight), new XAttribute("Italic", t.Italic), new XAttribute("Underline", t.Underline),
                    new XAttribute("StrikeOut", t.StrikeOut), new XAttribute("Anchor", t.Anchor), new XAttribute("Baseline", t.Baseline),
                    t.Advances is null ? null : new XAttribute("Advances", string.Join(" ", t.Advances.Select(N))),
                    t.VerticalAdvances is null ? null : new XAttribute("VerticalAdvances", string.Join(" ", t.VerticalAdvances.Select(N))),
                    // An attribute, not element content: XML loading drops whitespace-only content and
                    // normalizes CR/LF there, which would no longer match Advances. Attributes escape both.
                    new XAttribute("Value", t.Value)))));
    }

    // Scenes saved before the Value attribute keep their text as element content.
    private static string TextValue(XElement text) => (string?)text.Attribute("Value") ?? text.Value;

    public static ReportVectorImage? Read(XElement? xml)
    {
        if (xml is null) return null;
        if (xml.Elements().Any(e => e.Name != "Shape") || xml.Elements().Take(10001).Count() > 10000)
            throw new InvalidDataException("Unsupported vector scene content.");
        var result = new ReportVectorImage { Left = D(xml, "Left"), Top = D(xml, "Top"), Width = D(xml, "Width"), Height = D(xml, "Height") };
        var count = 0;
        foreach (var shape in xml.Elements())
        {
            if (shape.Elements().Any(e => e.Name != "Text" && e.Name != "ClipPolygon") || shape.Elements("Text").Skip(1).Any()
                || shape.Elements("ClipPolygon").Take(65).Count() > 64)
                throw new InvalidDataException("Unsupported vector shape content.");
            var points = Numbers((string?)shape.Attribute("Points") ?? "");
            if ((count += points.Length) > 200000) throw new InvalidDataException("Vector coordinate count exceeds the limit.");
            var polygons = new List<double[]>();
            foreach (var polygon in shape.Elements("ClipPolygon"))
            {
                var values = Numbers((string?)polygon.Attribute("Points") ?? "");
                if (values.Length > 128 || (count += values.Length) > 200000) throw new InvalidDataException("Vector clip data exceeds the limit.");
                polygons.Add(values);
            }
            result.Shapes.Add(new((string?)shape.Attribute("Kind") ?? "", points,
                (string?)shape.Attribute("Fill") ?? "none", (string?)shape.Attribute("Stroke") ?? "none", D(shape, "StrokeWidth"), (bool?)shape.Attribute("Winding") ?? false,
                shape.Attribute("Clip") is { } clip ? Numbers(clip.Value) : null,
                shape.Attribute("Transform") is { } transform ? Numbers(transform.Value) : null,
                shape.Element("Text") is not { } t ? null : new(TextValue(t), (string?)t.Attribute("Font") ?? "", D(t, "Size"), (int?)t.Attribute("Weight") ?? 400,
                    (bool?)t.Attribute("Italic") ?? false, (bool?)t.Attribute("Underline") ?? false, (bool?)t.Attribute("StrikeOut") ?? false,
                    (string?)t.Attribute("Anchor") ?? "start", (string?)t.Attribute("Baseline") ?? "alphabetic", t.Attribute("Advances") is { } dx ? Numbers(dx.Value) : null,
                    t.Attribute("VerticalAdvances") is { } dy ? Numbers(dy.Value) : null),
                polygons.ToArray()));
            if (shape.Element("Text") is { } content && (count += TextValue(content).Length) > 200000)
                throw new InvalidDataException("Vector text exceeds the limit.");
        }
        result.Validate(); return result;
        static double D(XElement e, string name) => double.Parse((string?)e.Attribute(name) ?? "0", CultureInfo.InvariantCulture);
        static double[] Numbers(string value)
        {
            if (value.Length > 2_000_000) throw new InvalidDataException("Vector coordinate data exceeds the limit.");
            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 200000) throw new InvalidDataException("Vector coordinate count exceeds the limit.");
            return parts.Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        }
    }

    public string ToDataUrl()
    {
        Validate();
        XNamespace ns = "http://www.w3.org/2000/svg";
        var scale = Math.Min(1, 2048 / Math.Max(Width, Height));
        var svg = new XElement(ns + "svg", new XAttribute("width", N(Math.Max(1, Math.Ceiling(Width * scale)))),
            new XAttribute("height", N(Math.Max(1, Math.Ceiling(Height * scale)))), new XAttribute("viewBox", $"{N(Left)} {N(Top)} {N(Width)} {N(Height)}"),
            new XAttribute("preserveAspectRatio", "none"));
        var defs = new XElement(ns + "defs"); svg.Add(defs);
        var polygonClipId = 0;
        for (var index = 0; index < Shapes.Count; index++)
        {
            var shape = Shapes[index]; var p = shape.Points;
            var node = shape.Kind switch
            {
                "Rectangle" => Rectangle(p),
                "Ellipse" => new XElement(ns + "ellipse", new XAttribute("cx", N(p[0] + p[2] / 2)), new XAttribute("cy", N(p[1] + p[3] / 2)), new XAttribute("rx", N(p[2] / 2)), new XAttribute("ry", N(p[3] / 2))),
                "Text" => Text(shape.Text!, p),
                "Bezier" => new XElement(ns + "path", new XAttribute("d", "M " + N(p[0]) + " " + N(p[1]) + " C " + string.Join(" ", p.Skip(2).Select(N)))),
                _ => new XElement(ns + (shape.Kind == "Polygon" ? "polygon" : "polyline"), new XAttribute("points", string.Join(" ", p.Select(N))))
            };
            node.Add(new XAttribute("fill", shape.Fill), new XAttribute("stroke", shape.Stroke), new XAttribute("stroke-width", N(shape.StrokeWidth)),
                new XAttribute("fill-rule", shape.Winding ? "nonzero" : "evenodd"), new XAttribute("stroke-linejoin", "round"), new XAttribute("stroke-linecap", "round"));
            if (shape.Transform is { } transform) node.Add(new XAttribute("transform", "matrix(" + string.Join(" ", transform.Select(N)) + ")"));
            if (shape.Clip is { } clip)
            {
                var id = "c" + index.ToString(CultureInfo.InvariantCulture);
                defs.Add(new XElement(ns + "clipPath", new XAttribute("id", id), Rectangle(clip)));
                node = new XElement(ns + "g", new XAttribute("clip-path", "url(#" + id + ")"), node);
            }
            foreach (var polygon in shape.ClipPolygons ?? [])
            {
                var id = "p" + (polygonClipId++).ToString(CultureInfo.InvariantCulture);
                defs.Add(new XElement(ns + "clipPath", new XAttribute("id", id), new XElement(ns + "polygon", new XAttribute("points", string.Join(" ", polygon.Select(N))))));
                node = new XElement(ns + "g", new XAttribute("clip-path", "url(#" + id + ")"), node);
            }
            svg.Add(node);
        }
        return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg.ToString(SaveOptions.DisableFormatting)));
        XElement Rectangle(double[] p) => new(ns + "rect", new XAttribute("x", N(p[0])), new XAttribute("y", N(p[1])), new XAttribute("width", N(p[2])), new XAttribute("height", N(p[3])));
        XElement Text(ReportVectorText text, double[] p)
        {
            var x = N(p[0]);
            if (text.Advances is { } dx)
            {
                var cursor = p[0] - dx.Sum() * (text.Anchor == "end" ? 1 : text.Anchor == "middle" ? .5 : 0);
                x = string.Join(" ", dx.Select(advance => { var position = cursor; cursor += advance; return N(position); }));
            }
            var y = N(p[1]);
            if (text.VerticalAdvances is { } dy)
            {
                var cursor = p[1] - dy.Sum() * (text.Anchor == "end" ? 1 : text.Anchor == "middle" ? .5 : 0);
                y = string.Join(" ", dy.Select(advance => { var position = cursor; cursor += advance; return N(position); }));
            }
            return new(ns + "text", new XAttribute("x", x), new XAttribute("y", y), new XAttribute(XNamespace.Xml + "space", "preserve"),
                new XAttribute("font-family", text.FontFamily), new XAttribute("font-size", N(text.FontSize)), new XAttribute("font-weight", text.Weight),
                new XAttribute("font-style", text.Italic ? "italic" : "normal"), new XAttribute("text-anchor", text.Advances is null ? text.Anchor : "start"),
                new XAttribute("dominant-baseline", text.Baseline), new XAttribute("text-decoration", string.Join(" ", new[] { text.Underline ? "underline" : "", text.StrikeOut ? "line-through" : "" }.Where(s => s.Length > 0))), text.Value);
        }
    }
    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
