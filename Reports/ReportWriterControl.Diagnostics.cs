using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Fx.ControlKit.Reports;

public partial class ReportWriterControl
{
    private readonly ConditionalWeakTable<ReportTab, HashSet<string>> _loggedRuntimeDiagnostics = new();

    private void LogRuntimeDiagnostics()
    {
        if (!Logger.IsEnabled(LogLevel.Information)) return;

        foreach (var tab in Tabs)
        {
            if (tab.RuntimeDiagnostics.Count == 0) continue;
            var logged = _loggedRuntimeDiagnostics.GetValue(tab, _ => new(StringComparer.Ordinal));
            foreach (var diagnostic in tab.RuntimeDiagnostics)
            {
                if (!string.IsNullOrWhiteSpace(diagnostic) && logged.Add(diagnostic))
                    Logger.LogInformation("Report runtime diagnostic for {ReportTitle}: {Diagnostic}", tab.Title, diagnostic);
            }
        }
    }
}
