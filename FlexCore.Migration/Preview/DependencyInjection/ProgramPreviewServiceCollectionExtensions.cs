using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fx.ControlKit.Preview;

public static class ProgramPreviewServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IProgramPreviewHost"/> as a singleton. The host stops every
    /// program when the service provider is disposed. Call it from the Blazor Server host.
    /// GUI sessions need the packages described in <c>docs/program-preview.md</c>.
    /// </summary>
    public static IServiceCollection AddFlexCoreProgramPreview(this IServiceCollection services, Action<ProgramPreviewOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp =>
        {
            var options = new ProgramPreviewOptions();
            configure?.Invoke(options);
            options.Validate();
            return options;
        });
        services.TryAddSingleton<ILogger<ProgramPreviewHost>>(sp =>
            sp.GetService<ILoggerFactory>()?.CreateLogger<ProgramPreviewHost>() ?? NullLogger<ProgramPreviewHost>.Instance);
        services.TryAddSingleton<ProgramPreviewHost>();
        services.TryAddSingleton<IProgramPreviewHost>(sp => sp.GetRequiredService<ProgramPreviewHost>());
        return services;
    }
}
