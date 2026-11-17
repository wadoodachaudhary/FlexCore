using Fx.ControlKit.Llm.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fx.ControlKit.Llm.CodeTools;

public static class CodeToolServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ICodeToolService"/>. Call <c>AddFlexCoreLlm</c> first so
    /// <see cref="ILlmClient"/> and <see cref="Configuration.ICredentialResolver"/> exist.
    /// Options bind from <c>Llm:CodeTools</c> when <paramref name="configuration"/> is passed.
    /// </summary>
    public static IServiceCollection AddFlexCoreCodeTools(this IServiceCollection services, IConfiguration? configuration = null, Action<CodeToolOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<CodeToolOptions>();
        if (configuration is not null)
            services.Configure<CodeToolOptions>(configuration.GetSection(CodeToolOptions.SectionName));
        if (configure is not null)
            services.Configure(configure);
        services.TryAddSingleton<ICodeToolService>(sp => new CodeToolService(
            sp.GetRequiredService<ILlmClient>(),
            sp.GetService<ICredentialResolver>(),
            sp.GetRequiredService<IOptions<CodeToolOptions>>().Value,
            sp.GetService<ILogger<CodeToolService>>()));
        return services;
    }
}
