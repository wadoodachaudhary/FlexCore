namespace Fx.ControlKit.Reports;

/// <summary>
/// Optional integration for rendering and exporting Crystal Reports (.rpt).
/// Implementations typically wrap SAP's Crystal Reports Engine; the host app
/// registers the implementation in DI <em>only</em> if Crystal is available.
///
/// <para>
/// <see cref="ReportWriterControl"/> uses this for two paths:
///   <list type="bullet">
///     <item><description><b>Direct .rpt rendering</b> — when a report has no XML sibling, load + parameterize + export-to-PDF, then display in an iframe.</description></item>
///     <item><description><b>Export of XML/SQL reports</b> — currently delegates to the same engine; future versions may produce PDF/CSV without it.</description></item>
///   </list>
/// </para>
/// </summary>
public interface IReportExporter
{
    /// <summary>True when an underlying engine is available and a report is currently loaded.</summary>
    bool IsLoaded { get; }

    /// <summary>Loads a Crystal Reports .rpt file into the engine's internal state.</summary>
    void LoadReport(string rptFilePath);

    /// <summary>Sets the named parameters on the currently-loaded report.</summary>
    void SetParameters(Dictionary<string, string> parameters);

    /// <summary>Renders the loaded report as PDF bytes.</summary>
    byte[] ExportToPdf();

    /// <summary>Renders the loaded report as Excel bytes (.xls).</summary>
    byte[] ExportToExcel();

    /// <summary>Renders the loaded report as Word bytes (.doc).</summary>
    byte[] ExportToWord();

    /// <summary>Renders the loaded report as RTF bytes.</summary>
    byte[] ExportToRtf();

    /// <summary>Renders the loaded report as CSV bytes.</summary>
    byte[] ExportToCsv();
}
