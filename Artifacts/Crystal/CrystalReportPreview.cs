using System.Data;
using Fx.ControlKit.Reports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fx.ControlKit.Artifacts.Crystal;

/// <summary>
/// Pages produced by the positioned viewer with no browser measurement.
/// Uses the same loader and <see cref="ReportLayoutSession"/> as <see cref="CrystalXmlViewer"/>.
/// </summary>
public static class CrystalReportPreview
{
    public static int CountPages(string xmlPath, DataTable data, ILogger<CrystalXmlReportLoader>? logger = null)
    {
        var loader = new CrystalXmlReportLoader(logger ?? NullLogger<CrystalXmlReportLoader>.Instance, new ReportOptions());
        var definition = loader.LoadPositioned(xmlPath);
        if (definition.PositionedLayout is null) return 0;
        return new ReportLayoutSession(definition.PositionedLayout, data).Paginate().Pages.Count;
    }
}
