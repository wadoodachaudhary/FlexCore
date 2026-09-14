# FlexCore.Llm

Provider-neutral LLM access for FlexCore applications. One `ILlmClient` routes
chat, streaming, tool use, JSON mode, embeddings and image generation to any
configured provider through raw `HttpClient` wire adapters — no provider SDKs,
no Azure.Identity, no UI dependency.

| Key            | Adapter                        | Wire format                                   |
|----------------|--------------------------------|-----------------------------------------------|
| `openai`       | `OpenAiResponsesProvider`      | Responses API, `/images/generations`, `/embeddings` |
| `azureopenai`  | `AzureOpenAiProvider`          | Chat Completions, deployment in path, `api-version` query |
| `azurefoundry` | `OpenAiCompatibleChatProvider` | Chat Completions, `api-key` header             |
| `anthropic`    | `AnthropicMessagesProvider`    | `/v1/messages`, SSE                            |
| `gemini`       | `GeminiProvider`               | `generateContent` / `streamGenerateContent?alt=sse` |
| `ollama`       | `OllamaProvider`               | `/api/chat` NDJSON, `/api/tags`, `/api/embed`  |
| `ollamacloud`  | `OllamaProvider`               | same, independent endpoint + auth              |
| `huggingface`  | `OpenAiCompatibleChatProvider` | router `/v1/chat/completions`                  |
| `groq`, `xai`, `mistral` | `OpenAiCompatibleChatProvider` | `/v1/chat/completions`               |

## Registering

```csharp
builder.Services.AddFlexCoreLlm(builder.Configuration, llm => llm
    .AddLoggingObserver()
    .UseTokenProvider(ct => credential.GetTokenAsync(scope, ct)));   // optional: Entra for Azure
```

## Calling

```csharp
var result = await client.ChatAsync("anthropic:claude-sonnet-4-6", "Summarise this.", system: "Be brief.");
Console.WriteLine(result.Text);            // result.Usage, result.FinishReason, result.IsTruncated

await foreach (var delta in client.StreamAsync("ollama:qwen2.5-coder:32b", prompt))
    Console.Write(delta.TextDelta);        // the final delta carries Usage + FinishReason
```

Model references are `provider:model`; a bare id (`gpt-5.4`, `claude-opus-4-7`,
`qwen3:8b`) is resolved through `ModelCatalog.InferProvider`. `claude:`,
`grok:`, `mistralai:` and `cloud-ollama:` prefixes are accepted aliases.

## Configuration

Environment variables are read first, then the `Llm` section. Files therefore
carry endpoints, models and the *name* of the variable that holds a key — not
the key.

```json
"Llm": {
  "TimeoutSeconds": 120,
  "Retry": { "MaxAttempts": 3, "BaseDelaySeconds": 1, "MaxDelaySeconds": 30 },
  "OpenAi":      { "ApiKeyVariable": "OPENAI_API_KEY", "DefaultModel": "gpt-5.4" },
  "Anthropic":   { "DefaultModel": "claude-sonnet-4-6", "Models": ["claude-opus-4-7", "claude-haiku-4-5"] },
  "AzureOpenAi": { "Endpoint": "https://my-res.openai.azure.com", "Deployment": "gpt41", "ApiVersion": "2024-10-21" },
  "Ollama":      { "Endpoint": "http://localhost:11434" },
  "OllamaCloud": { "Endpoint": "https://ollama.example.com", "AuthMode": "Bearer", "ApiKeyVariable": "OLLAMA_CLOUD_API_KEY" }
}
```

Per-provider keys: `Endpoint`/`BaseUrl`, `ApiKey`, `ApiKeyVariable`, `AuthMode`
(`None`, `Bearer`, `ApiKeyHeader`, `Basic`, `Custom`; legacy `api-key`,
`header`, `managedidentity`, `Auto` are understood), `HeaderName`, `ApiVersion`,
`Deployment`, `TokenScope`, `DefaultModel`, `Models`, `TimeoutSeconds`,
`MaxOutputTokens`, `Username`, `Password`, `Headers`.

The environment variables each provider honours are listed in
`LlmEnvironmentVariables` (`OPENAI_API_KEY`, `AZURE_OPENAI_API_KEY`,
`AZURE_OPENAI_ENDPOINT`, `ANTHROPIC_API_KEY`/`CLAUDE_API_KEY`,
`GEMINI_API_KEY`/`GOOGLE_API_KEY`, `GROQ_API_KEY`, `XAI_API_KEY`/`GROK_API_KEY`,
`MISTRAL_API_KEY`, `HUGGINGFACE_API_KEY`/`HF_TOKEN`, `OLLAMA_ENDPOINT`/`OLLAMA_HOST`,
`OLLAMA_API_KEY`, `OLLAMA_AUTH_MODE`, `OLLAMA_AUTH_HEADER`, `OLLAMA_CLOUD_ENDPOINT`,
`OLLAMA_CLOUD_API_KEY`, …).

## Behaviour

- **Retry** — 429 (honouring `Retry-After`), 502, 503, 504, 529, "overloaded"
  bodies and connection failures, exponential back-off with jitter; a stream is
  retried only before its first delta.
- **Timeout** — `ChatRequest.Timeout` → provider `TimeoutSeconds` →
  `Llm:TimeoutSeconds`, applied through a linked token and raised as
  `LlmTimeoutException`; the caller's own cancellation stays an
  `OperationCanceledException`. Streams treat it as an idle timeout.
- **Observers** — `ILlmCallObserver` receives started / completed / failed /
  retrying with model, elapsed, usage and prompt *length* (never content).
  `AddLoggingObserver()` logs each call with the estimated USD cost from
  `ILlmPricing`.
- **Capabilities** — `ILlmProvider.Capabilities` says what an adapter supports;
  members outside that set throw `NotSupportedException`.
- **Usage** — `LlmUsage.Input` excludes cached tokens, which are reported in
  `CacheRead` (and `CacheWrite` for Anthropic).
- **Finish reasons** — normalised to `FinishReasons.Stop` / `MaxTokens` /
  `ToolCalls` / `ContentFilter`; `ChatResult.IsTruncated` flags a token-cap cut.
