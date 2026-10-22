namespace Fx.ControlKit.Reports;

public sealed partial class ReportLayoutSession
{
    // A wrapper distinguishes a captured NULL from a field that failed evaluation.
    private sealed record DuplicateFieldValue(object? Value);
    private static bool SuppressesDuplicates(ReportDesignerElement element) => element.Kind == "Field"
        && (element.SuppressIfDuplicate || element.NativeNumericFormat?.SuppressIfDuplicate == true
            || element.NativeNumberFormat?.SuppressIfDuplicate == true || element.NativeDateTimeFormat?.SuppressIfDuplicate == true);

    private (string Html, DuplicateFieldValue? Value) CaptureFieldContent(ReportDesignerElement element, int row)
    {
        DuplicateFieldValue? value = null;
        var html = ReportObjectRenderer.Content(element, reference => Format(reference, row, element.FormatString,
            element.NativeDateTimeFormat, element.NativeNumberFormat, element.NativeNumericFormat,
            SuppressesDuplicates(element) ? raw => value = new(raw) : null));
        return (html, html.Contains("[Formula error:", StringComparison.Ordinal) ? null : value);
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
            var suppressed = seen && !firstDetail && Equals(previous.Value, value.Value);
            _previous[key] = (item.Row, occurrence, value.Value, suppressed);
            return suppressed;
        }
    }
}
