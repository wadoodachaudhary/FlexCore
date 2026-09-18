using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

internal static class ReportObjectCapabilities
{
    internal static string? UnsupportedCrystalKind(string? kind, string? elementName = null)
    {
        if (string.Equals(elementName, "ChartObject", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "Chart", StringComparison.OrdinalIgnoreCase)) return "Chart";
        if (string.Equals(elementName, "CrossTabObject", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "CrossTab", StringComparison.OrdinalIgnoreCase)) return "CrossTab";
        return null;
    }

    internal static string Diagnostic(string report, string section, string name, string kind, bool positioned, XElement? source = null)
        => $"[CRYSTAL_UNSUPPORTED_OBJECT] {report}/{section}/{name}: {kind} objects are not implemented. "
            + (positioned ? "Positioned output uses an unsupported-object placeholder when visible; suppression still applies. "
                : "Tabular output omits this object. ")
            + "Source metadata is retained in the report XML; chart data/series and cross-tab grouping/cells are not evaluated."
            + ((string?)source?.Element("NativeCrystalSource")?.Attribute("MetadataDiagnostic") is { Length: > 0 } warning
                ? " Native source: " + warning : "");

    internal static IEnumerable<string> ReadDiagnostics(XElement report, bool positioned)
    {
        foreach (var section in report.Element("ReportDefinition")?.Elements("Areas").Elements("Area").Elements("Sections").Elements("Section") ?? [])
        foreach (var obj in section.Elements("ReportObjects").Elements())
            if (UnsupportedCrystalKind((string?)obj.Attribute("Kind"), obj.Name.LocalName) is { } kind
                && (!positioned || ReportAnalysisDefinition.Read(obj)?.Validate(kind) is not null || obj.Element("FlexKitAnalysis") is null))
                yield return Diagnostic((string?)report.Attribute("Name") ?? "Report", (string?)section.Attribute("Name") ?? "Section",
                    (string?)obj.Attribute("Name") ?? obj.Name.LocalName, kind, positioned, obj);
    }
}
