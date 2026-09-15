namespace Fx.ControlKit.Llm;

public enum ProbeStatus
{
    /// <summary>No provider is registered under that key.</summary>
    NotRegistered,
    /// <summary>The provider has no key/endpoint (<see cref="ILlmProvider.IsConfigured"/> is false) or no usable default model.</summary>
    NotConfigured,
    /// <summary>An authenticated call succeeded.</summary>
    Ok,
    /// <summary>The endpoint answered 401/403: the credential is wrong or lacks access.</summary>
    AuthFailed,
    /// <summary>No HTTP answer within the probe timeout (DNS, connection, or the daemon is down).</summary>
    Unreachable,
    /// <summary>The endpoint answered with another error.</summary>
    Failed,
}

/// <summary>
/// Result of <see cref="ILlmClient.ProbeAsync"/>: one cheap authenticated
/// call (the model list where the provider has one, otherwise a one-token
/// chat with the default model) so a settings page can show
/// configured / reachable / rejected per provider.
/// </summary>
public sealed record ProviderProbe(
    string Provider,
    ProbeStatus Status,
    bool IsConfigured,
    TimeSpan Elapsed,
    int? ModelCount = null,
    ModelRef? Model = null,
    string? Message = null)
{
    public bool IsReachable => Status is ProbeStatus.Ok or ProbeStatus.AuthFailed or ProbeStatus.Failed;
    public bool IsOk => Status == ProbeStatus.Ok;
}
