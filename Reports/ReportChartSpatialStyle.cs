using System.Globalization;
using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

/// <summary>Saved analytical drawing options, shared by the designer and PDF renderer.</summary>
public sealed record ReportChartSpatialStyle
{
    public int Family { get; init; }
    public bool ProportionalRings { get; init; }
    public bool Horizontal { get; init; }
    public string DepthTitle { get; init; } = "";
    public string CategoryTitle { get; init; } = "";
    public string ValueTitle { get; init; } = "";
    public int PieDetachment { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReportBottomPieLayout? BottomPie { get; init; }
    public string Palette { get; init; } = "";
    public string Camera { get; init; } = "";
    public ReportGaugeArc? GaugeArc { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReportSpatialColumnView? ColumnView { get; init; }
    public string Volume { get; init; } = "";
    public double[] VolumeValues => ParseNumbers(Volume);
    public ReportPlotFrame? Frame { get; init; }
    public ReportChartScale? PrimaryScale { get; init; }
    public ReportChartScale? SecondaryScale { get; init; }
    public bool IsValid => Family is 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 10 or 11 or 12 or 15 && PieDetachment is >= 0 and <= 3
        && (ColumnView is null || Family is (5 or 6) && ColumnView.IsValid)
        && (GaugeArc is null || Family == 12 && GaugeArc.Start is >= 0 and < 360 && GaugeArc.End > GaugeArc.Start && GaugeArc.End <= 360 && GaugeArc.Intervals is >= 2 and <= 40)
        && (Camera.Length == 0 || CameraValues is { Length: 12 } values && values.All(double.IsFinite)) && (Volume.Length == 0 || VolumeValues is { Length: 3 } size && size.All(n => double.IsFinite(n) && n > 0)) && (Frame is null || Frame.IsValid) && (BottomPie is null || Family == 3 && Frame is not null && BottomPie.IsValid);
    public double[] CameraValues => ParseNumbers(Camera);
    private static double[] ParseNumbers(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
    internal XElement ToXml() => new("SpatialStyle", new XAttribute("Family", Family), ProportionalRings ? new XAttribute("ProportionalRings", true) : null, Horizontal ? new XAttribute("Horizontal", true) : null, DepthTitle.Length > 0 ? new XAttribute("DepthTitle", DepthTitle) : null, new XAttribute("CategoryTitle", CategoryTitle),
        new XAttribute("ValueTitle", ValueTitle), new XAttribute("PieDetachment", PieDetachment), new XAttribute("Palette", Palette), new XAttribute("Camera", Camera), new XAttribute("Volume", Volume),
        GaugeArc is { } arc ? new XElement("GaugeArc", new XAttribute("Start", arc.Start), new XAttribute("End", arc.End), new XAttribute("Intervals", arc.Intervals)) : null,
        ColumnView?.ToXml(), BottomPie?.ToXml(), PrimaryScale?.ToXml("PrimaryScale"), SecondaryScale?.ToXml("SecondaryScale"),
        Frame is {} f ? new XElement("Frame", new XAttribute("Left",f.Left),new XAttribute("Top",f.Top),new XAttribute("Right",f.Right),new XAttribute("Bottom",f.Bottom)) : null);
    internal static ReportChartSpatialStyle? Read(XElement? x) => x is null ? null : new()
    {
        Family = (int?)x.Attribute("Family") ?? 0, ProportionalRings = (bool?)x.Attribute("ProportionalRings") ?? false, Horizontal = (bool?)x.Attribute("Horizontal") ?? false, DepthTitle = (string?)x.Attribute("DepthTitle") ?? "", CategoryTitle = (string?)x.Attribute("CategoryTitle") ?? "", ValueTitle = (string?)x.Attribute("ValueTitle") ?? "",
        PieDetachment = (int?)x.Attribute("PieDetachment") ?? 0, Palette = (string?)x.Attribute("Palette") ?? "", Camera = (string?)x.Attribute("Camera") ?? "", Volume = (string?)x.Attribute("Volume") ?? "",
        GaugeArc = x.Element("GaugeArc") is { } arc ? new((int)arc.Attribute("Start")!, (int)arc.Attribute("End")!, (int?)arc.Attribute("Intervals") ?? 8) : null,
        ColumnView = ReportSpatialColumnView.Read(x.Element("ColumnView")),
        BottomPie = ReportBottomPieLayout.Read(x.Element("BottomPie")),
        PrimaryScale = ReportChartScale.Read(x.Element("PrimaryScale")), SecondaryScale = ReportChartScale.Read(x.Element("SecondaryScale")),
        Frame = x.Element("Frame") is {} f ? new((double)f.Attribute("Left")!, (double)f.Attribute("Top")!, (double)f.Attribute("Right")!, (double)f.Attribute("Bottom")!) : null
    };
}

/// <summary>Viewer and cube-wall dimensions in native graph units; pan uses the signed 32768-unit viewport.</summary>
public sealed record ReportSpatialColumnView(int EyeX, int EyeY, int EyeZ, int PanX, int PanY,
    int WallX, int FloorY, int WallZ, int GapPercent, bool ShowLeft, bool ShowFloor, bool ShowBack)
{
    internal bool IsValid => EyeZ != 0 && WallX >= 0 && FloorY >= 0 && WallZ >= 0 && GapPercent is >= 0 and <= 100;
    internal XElement ToXml() => new("ColumnView", new XAttribute("EyeX", EyeX), new XAttribute("EyeY", EyeY), new XAttribute("EyeZ", EyeZ),
        new XAttribute("PanX", PanX), new XAttribute("PanY", PanY), new XAttribute("WallX", WallX), new XAttribute("FloorY", FloorY),
        new XAttribute("WallZ", WallZ), new XAttribute("GapPercent", GapPercent), new XAttribute("ShowLeft", ShowLeft),
        new XAttribute("ShowFloor", ShowFloor), new XAttribute("ShowBack", ShowBack));
    internal static ReportSpatialColumnView? Read(XElement? x) => x is null ? null : new((int)x.Attribute("EyeX")!, (int)x.Attribute("EyeY")!,
        (int)x.Attribute("EyeZ")!, (int)x.Attribute("PanX")!, (int)x.Attribute("PanY")!, (int)x.Attribute("WallX")!, (int)x.Attribute("FloorY")!,
        (int)x.Attribute("WallZ")!, (int)x.Attribute("GapPercent")!, (bool)x.Attribute("ShowLeft")!, (bool)x.Attribute("ShowFloor")!, (bool)x.Attribute("ShowBack")!);
}

/// <summary>Authored pie geometry, per-slice label flags, and optional bottom legend bounds.</summary>
public sealed record ReportBottomPieLayout(int Tilt, int Depth, int Rotation, bool Clockwise, int HorizontalInset, int VerticalInset,
    int LabelFont, int LegendFont, string SliceLabels, ReportPlotFrame? Legend)
{
    public IReadOnlyDictionary<int, ReportPieLabelOffset>? LabelOffsets { get; init; }
    public IReadOnlyDictionary<int, ReportPieSliceStyle>? Slices { get; init; }
    public bool TransparentBackground { get; init; }

    internal bool IsValid => Tilt is >= 0 and < 90 && Depth is >= 0 and <= 3000 && Rotation is >= 0 and <= 360
        && HorizontalInset is >= 0 and < 100 && VerticalInset is >= 0 and < 100 && LabelFont > 0 && LegendFont > 0
        && SliceLabels.All(c => c is '0' or '1' or '2') && (Legend is null || Legend.IsValid);

    internal XElement ToXml() => new("BottomPie", new XAttribute("Tilt", Tilt), new XAttribute("Depth", Depth),
        new XAttribute("Rotation", Rotation), new XAttribute("Clockwise", Clockwise), new XAttribute("HorizontalInset", HorizontalInset),
        new XAttribute("VerticalInset", VerticalInset), new XAttribute("LabelFont", LabelFont), new XAttribute("LegendFont", LegendFont),
        new XAttribute("SliceLabels", SliceLabels), Legend is { } key ? new XAttribute[] { new("Left", key.Left),
            new("Top", key.Top), new("Right", key.Right), new("Bottom", key.Bottom) } : null,
        TransparentBackground ? new XAttribute("Transparent", true) : null,
        Slices?.Select(p => new XElement("Slice", new XAttribute("Index", p.Key), new XAttribute("Explode", p.Value.ExplodePercent), new XAttribute("ValueFormat", p.Value.ValueFormat))),
        LabelOffsets?.Select(pair => new XElement("LabelOffset", new XAttribute("Index", pair.Key),
            new XAttribute("X", pair.Value.X), new XAttribute("Y", pair.Value.Y))));
    internal static ReportBottomPieLayout? Read(XElement? x) => x is null ? null : new((int)x.Attribute("Tilt")!, (int)x.Attribute("Depth")!,
        (int)x.Attribute("Rotation")!, (bool)x.Attribute("Clockwise")!, (int)x.Attribute("HorizontalInset")!, (int)x.Attribute("VerticalInset")!,
        (int)x.Attribute("LabelFont")!, (int)x.Attribute("LegendFont")!, (string?)x.Attribute("SliceLabels") ?? "",
        x.Attribute("Left") is { } left
            ? new((double)left, (double)x.Attribute("Top")!, (double)x.Attribute("Right")!, (double)x.Attribute("Bottom")!) : null)
    {
        TransparentBackground = (bool?)x.Attribute("Transparent") ?? false,
        Slices = x.Element("Slice") is null ? null : x.Elements("Slice").ToDictionary(e => (int)e.Attribute("Index")!,
            e => new ReportPieSliceStyle(Math.Clamp((int)e.Attribute("Explode")!, 0, 100), (string?)e.Attribute("ValueFormat") ?? "")),
        LabelOffsets = x.Element("LabelOffset") is null ? null : x.Elements("LabelOffset").ToDictionary(e => (int)e.Attribute("Index")!,
            e => new ReportPieLabelOffset((int)e.Attribute("X")!, (int)e.Attribute("Y")!))
    };
}

public sealed record ReportGaugeArc(int Start, int End, int Intervals);

public sealed record ReportPieSliceStyle(int ExplodePercent, string ValueFormat);

public sealed record ReportPieLabelOffset(int X, int Y);

public sealed record ReportChartScale(double Minimum, double Maximum, int Intervals)
{
    public bool IsValid => double.IsFinite(Minimum) && double.IsFinite(Maximum) && Minimum < Maximum && Intervals is >= 1 and <= 64;
    internal XElement ToXml(string name) => new(name, new XAttribute("Min", Minimum), new XAttribute("Max", Maximum), new XAttribute("Intervals", Intervals));
    internal static ReportChartScale? Read(XElement? x) => x is null ? null : new((double)x.Attribute("Min")!, (double)x.Attribute("Max")!, (int)x.Attribute("Intervals")!);
}
