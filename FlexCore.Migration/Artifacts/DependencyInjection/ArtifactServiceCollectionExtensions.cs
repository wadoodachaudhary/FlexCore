using Fx.ControlKit.Artifacts.Crystal;
using Fx.ControlKit.Artifacts.Exstream;
using Fx.ControlKit.Reports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fx.ControlKit.Artifacts;

public static class ArtifactServiceCollectionExtensions
{
    /// <summary>
    /// Registers translated-file viewers and the Crystal sample-data host used by <see cref="CrystalXmlViewer"/>.
    /// <see cref="CrystalReportDataExecutor"/> is registered as <see cref="IReportDataExecutor"/>, so
    /// <see cref="ReportWriterControl"/> inside the viewer reads synthetic samples, a fixture, or an empty layout.
    /// Call this from the host that renders artifact viewers. A host that already executes report SQL
    /// should map <see cref="IReportDataExecutor"/> itself instead of replacing that registration.
    /// </summary>
    public static IServiceCollection AddFlexCoreArtifacts(this IServiceCollection services)
    {
        services.TryAddSingleton<ReportOptions>();
        services.TryAddScoped<CrystalXmlReportLoader>();
        services.AddScoped<CrystalReportDataExecutor>();
        services.AddScoped<IReportDataExecutor>(sp => sp.GetRequiredService<CrystalReportDataExecutor>());
        services.AddScoped<IReportDefinitionDataExecutor>(sp => sp.GetRequiredService<CrystalReportDataExecutor>());
        services.TryAddScoped<IReportSessionContext, EmptyReportSessionContext>();
        services.TryAddScoped<IReportViewerSettings, InMemoryReportViewerSettings>();
        services.TryAddScoped<IReportPickListProvider, EmptyReportPickListProvider>();
        services.TryAddScoped<IReportExporter, UnavailableCrystalExporter>();

        services.AddSingleton<ITranslatedFileViewer, CrystalXmlViewerDescriptor>();
        services.AddSingleton<ITranslatedFileViewer>(ExstreamPackViewerDescriptor.Pack);
        services.AddSingleton<ITranslatedFileViewer>(ExstreamPackViewerDescriptor.Document);
        services.AddSingleton<ITranslatedFileViewer>(ExstreamPackViewerDescriptor.Script);
        services.AddSingleton<ITranslatedFileViewer>(ExstreamPackViewerDescriptor.Resource);
        return services;
    }
}
