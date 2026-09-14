using System.Net;

namespace Fx.ControlKit.Llm;

/// <summary>Base class for every failure this library raises on its own behalf.</summary>
public class LlmException : Exception
{
    public LlmException(string message, string? providerKey = null, ModelRef? model = null, Exception? inner = null)
        : base(message, inner)
    {
        ProviderKey = providerKey;
        Model = model;
    }

    public string? ProviderKey { get; }
    public ModelRef? Model { get; }
}

/// <summary>The provider answered with a non-success status. <see cref="ResponseBody"/> is the raw body (never request headers).</summary>
public sealed class LlmHttpException : LlmException
{
    public LlmHttpException(
        string providerKey,
        HttpStatusCode statusCode,
        string? responseBody,
        TimeSpan? retryAfter = null,
        ModelRef? model = null,
        string? requestUrl = null)
        : base(BuildMessage(providerKey, statusCode, responseBody), providerKey, model)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
        RetryAfter = retryAfter;
        RequestUrl = requestUrl;
    }

    public HttpStatusCode StatusCode { get; }
    public string? ResponseBody { get; }
    public TimeSpan? RetryAfter { get; }
    public string? RequestUrl { get; }

    /// <summary>
    /// 429, 502, 503, 504, 529 and any body that reports an overloaded
    /// upstream. <see cref="RetryPolicy"/> retries exactly these.
    /// </summary>
    public bool IsTransient
    {
        get
        {
            var code = (int)StatusCode;
            if (code is 429 or 502 or 503 or 504 or 529) return true;
            return ResponseBody is not null &&
                   ResponseBody.Contains("overloaded", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A 403/404 whose body says the model is unknown to, or not enabled for,
    /// this API key (<c>model_not_found</c>, "does not have access to model",
    /// Anthropic's <c>not_found_error</c>). <see cref="ModelFallbackPolicy"/>
    /// moves to the next candidate model on exactly these.
    /// </summary>
    public bool IsModelAccessError
    {
        get
        {
            if (StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.NotFound)) return false;
            if (ResponseBody is null) return false;
            return ResponseBody.Contains("model_not_found", StringComparison.OrdinalIgnoreCase)
                || ResponseBody.Contains("does not have access to model", StringComparison.OrdinalIgnoreCase)
                || ResponseBody.Contains("do not have access to model", StringComparison.OrdinalIgnoreCase)
                || ResponseBody.Contains("does not exist or you do not have access", StringComparison.OrdinalIgnoreCase)
                || (ResponseBody.Contains("not_found_error", StringComparison.OrdinalIgnoreCase) && ResponseBody.Contains("model", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string BuildMessage(string providerKey, HttpStatusCode statusCode, string? body)
    {
        var snippet = string.IsNullOrWhiteSpace(body) ? string.Empty : body.Trim();
        if (snippet.Length > 600) snippet = snippet[..600] + "…";
        return snippet.Length == 0
            ? $"{providerKey} request failed with HTTP {(int)statusCode} {statusCode}."
            : $"{providerKey} request failed with HTTP {(int)statusCode} {statusCode}: {snippet}";
    }
}

/// <summary>The per-request timeout elapsed. Distinct from an <see cref="OperationCanceledException"/> raised by the caller's own token.</summary>
public sealed class LlmTimeoutException : LlmException
{
    public LlmTimeoutException(string providerKey, ModelRef model, TimeSpan timeout, Exception? inner = null)
        : base($"{providerKey}:{model.Model} did not answer within {timeout.TotalSeconds:0.#}s.", providerKey, model, inner)
    {
        Timeout = timeout;
    }

    public TimeSpan Timeout { get; }
}

/// <summary>A key, endpoint or model that the call needs is not configured.</summary>
public sealed class LlmConfigurationException : LlmException
{
    public LlmConfigurationException(string message, string? providerKey = null, ModelRef? model = null)
        : base(message, providerKey, model)
    {
    }
}

/// <summary>The provider returned a 2xx whose body could not be understood, or an in-band error event.</summary>
public sealed class LlmResponseException : LlmException
{
    public LlmResponseException(string message, string providerKey, ModelRef? model = null, string? rawJson = null, Exception? inner = null)
        : base(message, providerKey, model, inner)
    {
        RawJson = rawJson;
    }

    public string? RawJson { get; }
}
