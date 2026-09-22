using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Fx.ControlKit.Grid;

/// <summary>
/// Column width engine shared by <c>GridControl</c> and <c>TreeGridControl</c>: best fit
/// (size to content), fit to the grid (proportional scale to the available width), the
/// bounds and rounding both apply, the DOM measurement call, the estimate used when the
/// DOM cannot be measured, the grip double-click detection and the table width style.
/// The controls keep only what differs between them — how their columns, cell values,
/// widths and events are reached — and hand the numbers here.
/// </summary>
internal static class ColumnAutoFit
{
    public const double DefaultMinWidth = 40;
    public const double DefaultCeilingPx = 2000;
    public const int DefaultSampleSize = 50;
    /// <summary>Two grip mousedowns on the same column within this window are a double-click.</summary>
    public const int GripDoubleClickMs = 700;
    /// <summary>Width a column with no width at all is treated as while scaling.</summary>
    public const double FallbackWidthPx = 120;
    public const string JsMeasureFunction = "measureColumnContentWidths";
    public const string JsAvailableWidthFunction = "measureGridAvailableWidth";
    private const string ContainerWidthKey = "__fxContainerWidth";

    /// <summary>"123" / "123px" → 123; percentages and anything else → null (GridControl's
    /// own parse, unchanged, so a column's MinWidth/MaxWidth mean what they always did).</summary>
    public static double? TryParseWidthPx(string? width)
    {
        if (string.IsNullOrWhiteSpace(width))
            return null;
        var trimmed = width.Trim();
        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^2];
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var px)
            ? px
            : null;
    }

    /// <summary>Character-count estimate of a column's content width, used when the DOM
    /// could not be measured (prerender, torn-down circuit, grid not laid out yet).</summary>
    public static double EstimateWidth(string? headerText, IEnumerable<string?>? sampleTexts, bool isNumeric)
    {
        var maxLen = headerText?.Length ?? 0;
        if (sampleTexts != null)
        {
            foreach (var text in sampleTexts)
            {
                if (string.IsNullOrEmpty(text))
                    continue;
                if (text.Length > maxLen)
                    maxLen = text.Length;
            }
        }

        var px = (maxLen * 7.6) + 36;
        if (isNumeric)
            px += 12;
        return Math.Clamp(px, 80, 520);
    }

    /// <summary>A column's own MinWidth/MaxWidth beat the grid-wide defaults; px only,
    /// since a percentage cannot be compared against a measured pixel width.</summary>
    public static (double Min, double Max) ResolveBounds(string? minWidth, string? maxWidth, double gridMin, double gridMax, double ceilingPx)
    {
        var min = TryParseWidthPx(minWidth) ?? gridMin;
        var max = TryParseWidthPx(maxWidth) ?? (gridMax > 0 ? gridMax : ceilingPx);
        if (max < min) max = min;
        return (min, max);
    }

    /// <summary>Asks grid-control.js for the intrinsic content width of each field. Returns
    /// null when the DOM is unavailable; the caller then falls back to
    /// <see cref="EstimateWidth"/>. The scroller's client width, when known, is reported
    /// through <paramref name="onContainerWidth"/> — the ceiling for unbounded columns.</summary>
    public static async Task<Dictionary<string, double>?> MeasureAsync(
        IJSObjectReference? module, ElementReference host, IReadOnlyList<string> fields,
        int sampleSize, ColumnAutoFitDomOptions dom, Action<double>? onContainerWidth)
    {
        if (module == null || fields.Count == 0)
            return null;
        try
        {
            var measured = await module.InvokeAsync<Dictionary<string, double>?>(
                JsMeasureFunction, host, fields, Math.Max(1, sampleSize), dom.ToJs());
            if (measured != null && measured.TryGetValue(ContainerWidthKey, out var containerPx))
            {
                measured.Remove(ContainerWidthKey);
                if (containerPx > 80)
                    onContainerWidth?.Invoke(containerPx);
            }
            return measured;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The width the DATA columns can share: the scroll surface minus its non-data
    /// header cells (checkbox / selector / reorder) and the table's border overhead, all as
    /// rendered; 0 when the DOM is unavailable.</summary>
    public static async Task<double> MeasureAvailableWidthAsync(IJSObjectReference? module, ElementReference host, ColumnAutoFitDomOptions dom)
    {
        if (module == null)
            return 0;
        try
        {
            return await module.InvokeAsync<double>(JsAvailableWidthFunction, host, dom.Scroller, dom.FieldAttribute);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Best fit: each target sized to its measured (or estimated) content, clamped
    /// to its bounds and rounded. Only columns whose width actually changes are returned.</summary>
    public static List<ColumnWidthChange> ComputeBestFit(
        IReadOnlyList<ColumnAutoFitTarget> targets, IReadOnlyDictionary<string, double>? measured,
        double gridMin, double gridMax, double ceilingPx)
    {
        var changes = new List<ColumnWidthChange>();
        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            var content = measured != null
                          && target.Field.Length > 0
                          && measured.TryGetValue(target.Field, out var px)
                          && px > 0
                ? px
                : EstimateWidth(target.HeaderText, target.SampleTexts?.Invoke(), target.IsNumeric);

            var (min, max) = ResolveBounds(target.MinWidth, target.MaxWidth, gridMin, gridMax, ceilingPx);
            var width = Math.Round(Math.Clamp(content, min, max));
            if (Math.Abs(target.CurrentWidth - width) < 0.5)
                continue;
            changes.Add(new ColumnWidthChange(i, target.Field, target.CurrentWidth, width));
        }
        return changes;
    }

    /// <summary>Fit to grid: every target GROWS proportionally so together they exactly fill
    /// <paramref name="available"/> — unlike best fit, which sizes each column to its content.
    /// Columns that already reach or overflow the pane are left alone (owner 2026-09-21: the
    /// command fills free space; it never crushes a wide layout down to the pane and its
    /// scrollbar). The last column takes the rounding remainder; each column is floored at
    /// its minimum.</summary>
    public static List<ColumnWidthChange> ComputeFitToWidth(
        IReadOnlyList<ColumnAutoFitTarget> targets, double available,
        double gridMin, double gridMax, double ceilingPx)
    {
        var changes = new List<ColumnWidthChange>();
        if (targets.Count == 0 || available <= targets.Count * 20)
            return changes;

        var current = targets.Sum(t => t.CurrentWidth > 0 ? t.CurrentWidth : FallbackWidthPx);
        if (current <= 0 || current >= available)
            return changes;

        var factor = available / current;
        var assigned = 0d;
        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            var basis = target.CurrentWidth > 0 ? target.CurrentWidth : FallbackWidthPx;
            var width = i == targets.Count - 1
                ? Math.Round(available - assigned)
                : Math.Round(basis * factor);
            var (min, _) = ResolveBounds(target.MinWidth, target.MaxWidth, gridMin, gridMax, ceilingPx);
            width = Math.Max(width, min);
            assigned += width;
            if (Math.Abs(target.CurrentWidth - width) >= 0.5)
                changes.Add(new ColumnWidthChange(i, target.Field, target.CurrentWidth, width));
        }
        return changes;
    }

    /// <summary>Inline style of the table for a width mode. FitColumns keeps every column at
    /// its own width, so the table may end short of the container (slack after the last
    /// column) or run past it (horizontal scrollbar); FillAvailable stretches to the container
    /// and only overflows when the columns need more.</summary>
    public static string TableStyle(GridWidthMode mode, double totalWidthPx)
    {
        if (totalWidthPx <= 0)
            return mode == GridWidthMode.FitColumns ? "width:auto;min-width:0;" : "width:100%;";

        var widthPx = totalWidthPx.ToString("0.##", CultureInfo.InvariantCulture);
        return mode == GridWidthMode.FitColumns
            ? $"width:{widthPx}px;min-width:{widthPx}px;max-width:none;"
            : $"width:100%;min-width:{widthPx}px;max-width:none;";
    }
}

/// <summary>One column handed to <see cref="ColumnAutoFit"/>: identity, current width,
/// per-column bounds, and the sampled cell texts to estimate from when the DOM cannot be
/// measured (read lazily, so a successful measurement never formats a value).</summary>
internal sealed class ColumnAutoFitTarget
{
    public required string Field { get; init; }
    public string? HeaderText { get; init; }
    /// <summary>Current width in px; 0 when the column has none yet.</summary>
    public double CurrentWidth { get; init; }
    public string? MinWidth { get; init; }
    public string? MaxWidth { get; init; }
    public bool IsNumeric { get; init; }
    public Func<IEnumerable<string?>>? SampleTexts { get; init; }
}

/// <summary>A width the engine wants applied; <see cref="Index"/> is the column's position
/// in the targets list the engine was given.</summary>
internal readonly record struct ColumnWidthChange(int Index, string Field, double OldWidth, double NewWidth);

/// <summary>Where grid-control.js finds a control's header cells, header labels, data cells
/// and scroll surface — the only thing the measurement differs by between the two grids.</summary>
internal sealed record ColumnAutoFitDomOptions(
    string FieldAttribute, string HeaderText, string Scroller, string HeaderIcons,
    double? HeaderSlack, double? IconWidth)
{
    /// <summary>GridControl: its original fixed allowances (18px round the caption, 16px per icon).</summary>
    public static readonly ColumnAutoFitDomOptions Grid = new(
        "data-field", ".fx-header-text", ".fx-grid-content",
        ".fx-sort-icon, .fx-filter-icon, .fx-filter-applied-mark",
        HeaderSlack: 18, IconWidth: 16);

    /// <summary>TreeGridControl: header padding differs between compact and default, so the
    /// header cell's own edges and each icon's real width are measured.</summary>
    public static readonly ColumnAutoFitDomOptions TreeGrid = new(
        "data-tree-field", ".fx-treegrid-header-text", ".fx-treegrid-content",
        ".fx-treegrid-sort-glyph, .fx-treegrid-filter-button, .fx-treegrid-header-icon-button",
        HeaderSlack: null, IconWidth: null);

    public object ToJs() => new
    {
        fieldAttribute = FieldAttribute, headerText = HeaderText, scroller = Scroller,
        headerIcons = HeaderIcons, headerSlack = HeaderSlack, iconWidth = IconWidth
    };
}

/// <summary>
/// Double-click on a column's resize grip, detected from two mousedowns rather than the DOM's
/// dblclick event: a real hand jitters a pixel or two, which resizes the column, which fires
/// the layout event, which makes hosts that re-key their columns rebuild the header — and a
/// browser only fires dblclick when both clicks land on the SAME element, so the rebuilt grip
/// never sees one. mousedown always arrives before the rebuild, so this survives it.
/// </summary>
internal sealed class GripDoubleClickDetector
{
    private string? _field;
    private DateTime _at;

    /// <summary>Records a grip mousedown; true when it completes a double-click on
    /// <paramref name="field"/> (a third click then starts a fresh pair).</summary>
    public bool Register(string? field, DateTime now, int windowMs = ColumnAutoFit.GripDoubleClickMs)
    {
        var isDoubleClick = _field != null
            && string.Equals(_field, field, StringComparison.Ordinal)
            && (now - _at).TotalMilliseconds <= windowMs;
        _field = field;
        _at = now;
        if (isDoubleClick)
            _field = null;
        return isDoubleClick;
    }

    /// <summary>A deliberate drag is not half of a double-click.</summary>
    public void Reset() => _field = null;
}
