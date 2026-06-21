namespace Fx.ControlKit.Reports;

public interface IReportExporter
{
    bool IsLoaded { get; }

    void LoadReport(string rptFilePath);

    void SetParameters(Dictionary<string, string> parameters);

    byte[] ExportToPdf();

    byte[] ExportToExcel();

    byte[] ExportToWord();

    byte[] ExportToRtf();

    byte[] ExportToCsv();
}
