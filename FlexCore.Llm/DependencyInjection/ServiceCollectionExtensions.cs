using Fx.ControlKit.Llm.Configuration;
using Fx.ControlKit.Llm.Pricing;
using Fx.ControlKit.Llm.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fx.ControlKit.Llm.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ILlmClient"/>, every built-in provider, a named
    /// <see cref="HttpClient"/> per provider (with no client-side timeout —
    /// the library applies its own), the environment-first credential
    /// resolver, <see cref="ILlmPricing"/> and <see cref="ILlmContextBudget"/>.
    /// <see cref="LlmOptions"/> binds from the <c>Llm</c> section of
    /// <paramref name="configuration"/>.
    /// </summary>
    public static IServiceCollection AddFlexCoreLlm(this IServiceCollection services, IConfiguration configuration, Action<LlmBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<LlmOptions>().Bind(configuration.GetSection(LlmOptions.SectionName));
        return AddCore(services, configure);
    }

    /// <summary>Same registrations with options supplied in code instead of configuration.</summary>
    public static IServiceCollection AddFlexCoreLlm(this IServiceCollection services, Action<LlmOptions> configureOptions, Action<LlmBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);
        services.AddOptions<LlmOptions>().Configure(configureOptions);
        return AddCore(services, configure);
    }

    private static IServiceCollection AddCore(IServiceCollection services, Action<LlmBuilder>? configure)
    {
        services.AddOptions();
        services.AddLogging();

        foreach (var key in ProviderKeys.All)
        {
            services.AddHttpClient(LlmProviderBase.HttpClientName(key))
                .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);
        }

        services.TryAddSingleton<ICredentialResolver, EnvironmentFirstCredentialResolver>();
        services.TryAddSingleton<ILlmPricing>(LlmPricing.Default);
        services.TryAddSingleton<ILlmContextBudget>(LlmContextBudget.Default);
        services.TryAddSingleton(sp => RetryPolicy.FromOptions(sp.GetRequiredService<IOptions<LlmOptions>>().Value.Retry));

        services.AddSingleton<ILlmProvider>(sp => Configure(sp, new OpenAiResponsesProvider(
            sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ICredentialResolver>(), sp.GetService<ITokenProvider>(),
            sp.GetServices<ILlmRequestAuthenticator>(), sp.GetService<ILogger<OpenAiResponsesProvider>>())));
        services.AddSingleton<ILlmProvider>(sp => Configure(sp, new AzureOpenAiProvider(
            sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ICredentialResolver>(), sp.GetService<ITokenProvider>(),
            sp.GetServices<ILlmRequestAuthenticator>(), sp.GetService<ILogger<AzureOpenAiProvider>>())));
        services.AddSingleton<ILlmProvider>(sp => Configure(sp, new AnthropicMessagesProvider(
            sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ICredentialResolver>(), sp.GetService<ITokenProvider>(),
            sp.GetServices<ILlmRequestAuthenticator>(), sp.GetService<ILogger<AnthropicMessagesProvider>>())));
        services.AddSingleton<ILlmProvider>(sp => Configure(sp, new GeminiProvider(
            sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ICredentialResolver>(), sp.GetService<ITokenProvider>(),
            sp.GetServices<ILlmRequestAuthenticator>(), sp.GetService<ILogger<GeminiProvider>>())));

        foreach (var key in new[] { ProviderKeys.Ollama, ProviderKeys.OllamaCloud })
        {
            var ollamaKey = key;
            services.AddSingleton<ILlmProvider>(sp => Configure(sp, new OllamaProvider(
                ollamaKey, sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ICredentialResolver>(), sp.GetService<ITokenProvider>(),
                sp.GetServices<ILlmRequestAuthenticator>(), sp.GetService<ILogger<OllamaProvider>>())));
        }

        foreach (var key in new[] { ProviderKeys.AzureFoundry, ProviderKeys.HuggingFace, ProviderKeys.Groq, ProviderKeys.XAi, ProviderKeys.Mistral })
        {
            var compatibleKey = key;
            services.AddSingleton<ILlmProvider>(sp => Configure(sp, new OpenAiCompatibleChatProvider(
                compatibleKey, sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ICredentialResolver>(), sp.GetService<ITokenProvider>(),
                sp.GetServices<ILlmRequestAuthenticator>(), sp.GetService<ILogger<OpenAiCompatibleChatProvider>>())));
        }

        services.TryAddSingleton<ILlmClient>(sp => new LlmClient(
            sp.GetServices<ILlmProvider>(),
            sp.GetRequiredService<IOptions<LlmOptions>>(),
            sp.GetRequiredService<RetryPolicy>(),
            sp.GetRequiredService<ICredentialResolver>(),
            sp.GetServices<ILlmCallObserver>(),
            sp.GetService<ILogger<LlmClient>>()));

        configure?.Invoke(new LlmBuilder(services));
        return services;
    }

    private static T Configure<T>(IServiceProvider sp, T provider) where T : LlmProviderBase
    {
        var options = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
        provider.GlobalMaxOutputTokens = options.MaxOutputTokens;
        provider.GlobalTemperature = options.Temperature;
        return provider;
    }
}

/// <summary>Fluent hooks for the pieces a host supplies: Entra tokens, custom auth, observers, extra providers, HttpClient tweaks.</summary>
public sealed class LlmBuilder
{
    internal LlmBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }

    /// <summary>Bearer tokens for Azure OpenAI / Azure AI Foundry when no API key is configured — wrap a DefaultAzureCredential here; the library never references Azure.Identity.</summary>
    public LlmBuilder UseTokenProvider(Func<CancellationToken, Task<string>> getToken)
    {
        Services.Replace(ServiceDescriptor.Singleton<ITokenProvider>(new DelegateTokenProvider(getToken)));
        return this;
    }

    /// <summary>Scope-aware variant: receives the provider's configured <c>TokenScope</c>.</summary>
    public LlmBuilder UseTokenProvider(Func<string?, CancellationToken, Task<string>> getToken)
    {
        Services.Replace(ServiceDescriptor.Singleton<ITokenProvider>(new DelegateTokenProvider(getToken)));
        return this;
    }

    public LlmBuilder UseTokenProvider<T>() where T : class, ITokenProvider
    {
        Services.Replace(ServiceDescriptor.Singleton<ITokenProvider, T>());
        return this;
    }

    /// <summary>Signs requests for one provider when its AuthMode is <see cref="AuthMode.Custom"/> (or it has no key).</summary>
    public LlmBuilder UseAuthenticator(string providerKey, Func<HttpRequestMessage, CancellationToken, Task> authenticate)
    {
        Services.AddSingleton<ILlmRequestAuthenticator>(new DelegateRequestAuthenticator(providerKey, authenticate));
        return this;
    }

    public LlmBuilder AddObserver<T>() where T : class, ILlmCallObserver
    {
        Services.AddSingleton<ILlmCallObserver, T>();
        return this;
    }

    public LlmBuilder AddObserver(ILlmCallObserver observer)
    {
        Services.AddSingleton(observer);
        return this;
    }

    /// <summary>Logs every call at Information (Warning on failure) with provider, model, elapsed, usage and estimated cost.</summary>
    public LlmBuilder AddLoggingObserver() => AddObserver<LoggingLlmCallObserver>();

    /// <summary>Registers an extra or replacement adapter; a later registration for the same key wins.</summary>
    public LlmBuilder AddProvider<T>() where T : class, ILlmProvider
    {
        Services.AddSingleton<ILlmProvider, T>();
        return this;
    }

    public LlmBuilder AddProvider(ILlmProvider provider)
    {
        Services.AddSingleton(provider);
        return this;
    }

    public LlmBuilder AddProvider(Func<IServiceProvider, ILlmProvider> factory)
    {
        Services.AddSingleton(factory);
        return this;
    }

    /// <summary>Further configures the named <see cref="HttpClient"/> for one provider (proxy, handler, headers).</summary>
    public LlmBuilder ConfigureHttpClient(string providerKey, Action<IHttpClientBuilder> configure)
    {
        configure(Services.AddHttpClient(LlmProviderBase.HttpClientName(providerKey)));
        return this;
    }

    public LlmBuilder UseRetryPolicy(RetryPolicy policy)
    {
        Services.Replace(ServiceDescriptor.Singleton(policy));
        return this;
    }

    public LlmBuilder UsePricing<T>() where T : class, ILlmPricing
    {
        Services.Replace(ServiceDescriptor.Singleton<ILlmPricing, T>());
        return this;
    }

    public LlmBuilder UseContextBudget<T>() where T : class, ILlmContextBudget
    {
        Services.Replace(ServiceDescriptor.Singleton<ILlmContextBudget, T>());
        return this;
    }

    public LlmBuilder UseCredentialResolver<T>() where T : class, ICredentialResolver
    {
        Services.Replace(ServiceDescriptor.Singleton<ICredentialResolver, T>());
        return this;
    }
}

/// <summary>Built-in observer: one log line per call, with the estimated USD cost when the pricing table knows the model.</summary>
public sealed class LoggingLlmCallObserver : ILlmCallObserver
{
    private readonly ILogger<LoggingLlmCallObserver> _logger;
    private readonly ILlmPricing? _pricing;

    public LoggingLlmCallObserver(ILogger<LoggingLlmCallObserver> logger, ILlmPricing? pricing = null)
    {
        _logger = logger;
        _pricing = pricing;
    }

    public void OnCompleted(LlmCallContext call, LlmCallOutcome outcome)
    {
        var cost = outcome.Usage is { } usage ? _pricing?.EstimateCostUsd(outcome.Resolved, usage) : null;
        _logger.LogInformation(
            "llm {Operation} {Model} ok in {ElapsedMs}ms attempts={Attempts} prompt={PromptChars}ch out={OutputChars}ch in_tok={Input} out_tok={Output} cache_read={CacheRead} finish={Finish} cost={Cost}",
            call.Operation, outcome.Resolved, (long)outcome.Elapsed.TotalMilliseconds, outcome.Attempts, call.PromptLength, outcome.OutputLength,
            outcome.Usage?.Input, outcome.Usage?.Output, outcome.Usage?.CacheRead, outcome.FinishReason,
            cost is { } c ? c.ToString("$0.000000") : "—");
    }

    public void OnFailed(LlmCallContext call, Exception error, TimeSpan elapsed)
    {
        _logger.LogWarning(error, "llm {Operation} {Model} failed after {ElapsedMs}ms attempts={Attempts}: {Message}",
            call.Operation, call.Model, (long)elapsed.TotalMilliseconds, call.Attempt, error.Message);
    }

    public void OnRetrying(LlmCallContext call, Exception error, int nextAttempt, TimeSpan delay)
    {
        _logger.LogInformation("llm {Operation} {Model} retry {Attempt} in {DelayMs}ms: {Message}",
            call.Operation, call.Model, nextAttempt, (long)delay.TotalMilliseconds, error.Message);
    }
}
