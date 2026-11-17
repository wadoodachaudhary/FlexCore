using Fx.ControlKit.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fx.ControlKit.Telemetry;

public static class FlexTelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Registers FlexKit's generalized control logging, SQL query telemetry store, default telemetry data source,
    /// and unified ticket/diagnostic snapshot engine. Host applications can register custom implementations of
    /// <see cref="ITelemetryDataSource"/>, <see cref="ITelemetryCatalogProvider"/>, <see cref="ITelemetryTenantProvider"/>,
    /// or <see cref="ITelemetryExportHandler"/> before or after calling this method.
    /// </summary>
    public static IServiceCollection AddFlexTelemetry(
        this IServiceCollection services,
        Action<FlexControlLoggingOptions>? configureControlLogging = null)
    {
        services.AddFlexControlLogging(configureControlLogging);
        services.TryAddSingleton<FlexSqlTelemetryStore>();
        services.TryAddSingleton<IFlexSqlTelemetryProvider>(sp => sp.GetRequiredService<FlexSqlTelemetryStore>());
        services.TryAddSingleton<ITelemetryDataSource, DefaultFlexTelemetryDataSource>();
        services.TryAddScoped<IFlexDiagnosticSnapshotService, FlexDiagnosticSnapshotService>();
        return services;
    }
}
