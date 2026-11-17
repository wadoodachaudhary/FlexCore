namespace Fx.ControlKit.Reports;

public sealed partial class ReportLayoutSession
{
    // Retain the raw field value for duplicate suppression and native NULL decoration.
    // A wrapper distinguishes a captured NULL from a field that failed evaluation.
    private sealed record DuplicateFieldValue(object? Value);
    private static bool SuppressesDuplicates(ReportDesignerElement element) => element.Kind == "Field"
        && (element.SuppressIfDuplicate || element.NativeNumericFormat?.SuppressIfDuplicate == true
            || element.NativeNumberFormat?.SuppressIfDuplicate == true || element.NativeDateTimeFormat?.SuppressIfDuplicate == true);

    private (string Html, DuplicateFieldValue? Value) CaptureFieldContent(ReportDesignerElement element, int row)
    {
        // Physical-page replay must retain the same live link as the initial layout pass.
        if (element.Kind == "Subreport" && _layout.OnDemandSubreports.ContainsKey(element.Id))
            return (OnDemandCaptionHtml(element, row), null);
        // A label-only chart prints its category value for this record, rotated across its frame, as Crystal does.
        if (element.Kind == "Chart" && element.ChartMarker is { } marker)
            return (MarkerHtml(element, marker, row), null);
        DuplicateFieldValue? value = null;
        var html = ReportObjectRenderer.Content(element, reference => Format(reference, row, element.FormatString,
            element.NativeDateTimeFormat, element.NativeNumberFormat, element.NativeNumericFormat,
            SuppressesDuplicates(element) || element.Kind == "Field" && element.UseCrystalScreenMetrics
                ? raw => value = new(raw) : null),
            resolveRun: run => Format(run.Binding, row, element.FormatString, run.NativeDateTimeFormat ?? element.NativeDateTimeFormat,
                element.NativeNumberFormat, RunNumericFormat(element)));
        return (html, html.Contains("[Formula error:", StringComparison.Ordinal) ? null : value);
    }

    // A field embedded in a text object without a numeric format of its own prints with Crystal's system default number
    // format (grouped thousands, the regional decimals), as a field object saved without one would.
    private static readonly ReportNativeNumericFormat EmbeddedNumericDefault = new(SuppressIfZero: false, NegativeType: 1, ThousandsSeparators: true,
        LeadingZero: true, DecimalPlaces: 2, RoundingType: 9, CurrencySymbolType: 0, OneCurrencySymbolPerPage: false, CurrencyPosition: 0,
        ThousandSymbol: ",", DecimalSymbol: ".", CurrencySymbol: "$", AllowFieldClipping: false, AccountingFormat: false, ReverseSign: false,
        ZeroValue: "<Default Format>", SuppressIfNoHorizontalPages: false, Prefix: "", Suffix: "", SystemDefaultFormatting: true);
    private static readonly ReportNativeNumericFormat EmbeddedIntegerDefault = EmbeddedNumericDefault with { ValueType = 4 };
    private static ReportNativeNumericFormat? RunNumericFormat(ReportDesignerElement element)
        => element.NativeNumericFormat ?? (element.NativeNumberFormat is null ? EmbeddedNumericDefault : null);

    // The run is placed by its own text, so a value that depends on the page is laid out on each page. A binding the
    // page schedule refuses (a circular reference, print-state variables in a page formula) goes through Format, which
    // prints the formula error and its diagnostic as it does for every other object.
    private string MarkerHtml(ReportDesignerElement element, ReportChartMarker marker, int row)
    {
        try
        {
            if (_formulaPage is null && DependsOn(marker.Binding, true))
                return DeferredPageMarkup(marker.Binding, row, value => marker.Html(element, value));
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or InvalidCastException or OverflowException
            or DivideByZeroException or NotSupportedException or ArgumentException) { }
        return marker.Html(element, Format(marker.Binding, row, ""));
    }

    private sealed class DuplicateFields
    {
        private readonly Dictionary<(ReportLayoutSession Owner, string Element), (int Row, int Occurrence, object? Value, bool Suppressed)> _previous = [];
        private readonly Dictionary<(ReportLayoutSession Owner, string Section, int Page), int> _firstDetails = [];

        // This is presentation only, after pagination. Compare original values per
        // object/section iteration, leave the occupied space and data exports intact.
        // Crystal prints the first Detail occurrence on each physical page even if
        // equal to the previous page's last value. A split occurrence keeps its decision.
        public bool Suppress(Item item, ReportLayoutSession fallback, int page, bool repeated)
        {
            if (item.Element.Kind != "Field") return false;
            var owner = item.Owner ?? fallback;
            var section = owner._layout.Document.Sections.First(s => s.Id == item.Element.SectionId);
            var detailKey = (owner, section.Id, page);
            if (section.Kind == "Detail") _firstDetails.TryAdd(detailKey, item.Row);
            if (!SuppressesDuplicates(item.Element) || item.DuplicateValue is not { } value) return false;
            var key = (owner, item.Element.Id);
            var occurrence = repeated || section.Kind is "PageHeader" or "PageFooter" ? page : 0;
            var seen = _previous.TryGetValue(key, out var previous);
            if (seen && previous.Row == item.Row && previous.Occurrence == occurrence) return previous.Suppressed;
            var firstDetail = section.Kind == "Detail" && _firstDetails[detailKey] == item.Row;
            // A new group starts a fresh detail duplicate run, even when its first value
            // equals the preceding group's last value on the same physical page.
            var newGroup = seen && section.Kind == "Detail" && owner._layout.Document.Groups.Count > 0
                && owner.GroupStart(owner._layout.Document.Groups.Count - 1, previous.Row)
                    != owner.GroupStart(owner._layout.Document.Groups.Count - 1, item.Row);
            var suppressed = seen && !firstDetail && !newGroup && Equals(previous.Value, value.Value);
            _previous[key] = (item.Row, occurrence, value.Value, suppressed);
            return suppressed;
        }
    }
}
