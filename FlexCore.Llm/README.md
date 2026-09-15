# FlexCore.Llm

Provider-neutral LLM access for FlexCore applications. One `ILlmClient` routes
chat, streaming, tool use, JSON output, embeddings and image generation to any
configured provider through raw `HttpClient` wire adapters — no provider SDKs,
no Azure.Identity, no UI dependency. Around it sit the pieces an application
needs to run models responsibly: per-user model settings, request overrides,
a chunk planner for text that does not fit, a JSON repair parser, a
conversation helper, a reachability probe, and an audit-log record.

| Key            | Adapter                        | Wire format                                                | Lists models |
|----------------|--------------------------------|------------------------------------------------------------|--------------|
| `openai`       | `OpenAiResponsesProvider`      | Responses API, `/images/generations`, `/embeddings`; Azure hosts get `/openai/v1` + `api-key` | live |
| `azureopenai`  | `AzureOpenAiProvider`          | Chat Completions, deployment in path, `api-version` query  | configured only |
| `azurefoundry` | `OpenAiCompatibleChatProvider` | Chat Completions (`/models` + `api-version`, or `/openai/v1`), `api-key` header | configured only |
| `anthropic`    | `AnthropicMessagesProvider`    | `/v1/messages`, SSE; `/v1/models`                          | live |
| `gemini`       | `GeminiProvider`               | `generateContent` / `streamGenerateContent?alt=sse`        | live |
| `ollama`       | `OllamaProvider`               | `/api/chat` NDJSON, `/api/tags`, `/api/embed`              | live |
| `ollamacloud`  | `OllamaProvider`               | same protocol, independent endpoint + auth                 | live |
| `huggingface`  | `OpenAiCompatibleChatProvider` | router `/v1/chat/completions`, or a dedicated endpoint      | router only |
| `groq`, `xai`, `mistral` | `OpenAiCompatibleChatProvider` | `/v1/chat/completions`, `/v1/models`             | live |

Aliases accepted anywhere a key is: `claude`, `azure`, `google`, `grok`,
`mistralai`/`mistralapi`, `hf`, `cloud-ollama`, `foundry`.

## Setup

```csharp
builder.Services.AddFlexCoreLlm(builder.Configuration, llm => llm
    .AddLoggingObserver()                                    // one log line per call, with cost
    .AddInMemoryCallLog(capacity: 500)                       // InMemoryCallLog for a diagnostics page
    .AddCallRecorder(record => auditTable.Insert(record))    // LlmCallRecord → your store
    .UseTokenProvider(ct => credential.GetTokenAsync(scope, ct))   // Entra tokens for Azure when no key
    .UseModelFallback(ModelFallbackPolicy.OpenAiDefaults));  // opt-in: retry with the next model on 403/404
```

`AddFlexCoreLlm` registers `ILlmClient`, all eleven providers as
`ILlmProvider` singletons, a named `HttpClient` per provider (client-side
timeout disabled; the library owns timing), `ICredentialResolver`
(environment first, then options), `ILlmPricing`, `ILlmContextBudget`,
`ILlmRequestOverrides`, `IChunkPlanner`, `RetryPolicy` and
`IModelConfigStore` (a JSON store when `Llm:ModelConfigDirectory` is set,
otherwise a no-op store). Every one can be replaced through the builder
(`UsePricing<T>`, `UseChunkPlanner<T>`, `UseModelConfigStore<T>`,
`UseJsonModelConfigStore(path)`, `UseInMemoryModelConfigStore()`,
`UseRequestOverrides<T>`, `UseRetryPolicy(policy)`, `UseCredentialResolver<T>`,
`ConfigureHttpClient(key, b => …)`).

## Configuration

Environment variables are read first, then the `Llm` section. Files therefore
carry endpoints, models and the *name* of the variable that holds a key —
never the key itself.

```json
"Llm": {
  "TimeoutSeconds": 120,
  "MaxOutputTokens": null,
  "Temperature": null,
  "ModelConfigDirectory": "App_Data/llm",
  "Retry": {
    "MaxAttempts": 3, "BaseDelaySeconds": 1, "MaxDelaySeconds": 30, "UseJitter": true,
    "FallbackModels": ["gpt-5.4", "gpt-5.2", "gpt-5", "gpt-4.1", "gpt-4.1-mini", "gpt-4o-mini"],
    "FallbackProviders": ["openai"],
    "FallbackIncludesConfiguredModels": true
  },
  "RequestOverrides": [
    { "Match": "phi-4-mini-instruct", "MaxOutputTokens": 768, "TimeoutSeconds": 45, "RetryOnTimeout": true,
      "ChunkChars": 1000,
      "ContextLimits": { "SurroundingContext": 2000, "BlockSummaries": 1200, "StoryDatabaseContext": 1500, "StylisticExemplars": 1200 } }
  ],
  "OpenAi":       { "ApiKeyVariable": "OPENAI_API_KEY", "DefaultModel": "gpt-5.4", "Models": ["gpt-5.4", "gpt-5.4-mini", "gpt-5.4-nano", "gpt-5.5"] },
  "AzureOpenAi":  { "Endpoint": "https://my-res.openai.azure.com", "Deployment": "gpt41", "ApiVersion": "2024-10-21", "TokenScope": "https://cognitiveservices.azure.com/.default" },
  "AzureFoundry": { "Endpoint": "https://my-hub.services.ai.azure.com", "ApiVersion": "2024-05-01-preview", "Models": ["phi-4-mini-instruct", "deepseek-v3.2"] },
  "Anthropic":    { "DefaultModel": "claude-sonnet-4-6", "Models": ["claude-opus-4-7", "claude-haiku-4-5"], "ApiVersion": "2023-06-01" },
  "Gemini":       { "DefaultModel": "gemini-2.5-pro", "ApiVersion": "v1beta" },
  "Ollama":       { "Endpoint": "http://localhost:11434", "Aliases": { "qwen": "qwen3:32b" } },
  "OllamaCloud":  { "Endpoint": "https://ollama.example.com", "AuthMode": "Bearer", "ApiKeyVariable": "OLLAMA_CLOUD_API_KEY" },
  "HuggingFace":  { "Endpoint": "https://router.huggingface.co/v1", "DefaultModel": "Qwen/Qwen3-32B:nscale",
                    "DedicatedModel": "MaziyarPanahi/calme-3.2-instruct-78b", "DedicatedModels": [] },
  "Groq":         { "DefaultModel": "llama-3.3-70b-versatile" },
  "XAi":          { "DefaultModel": "grok-4" },
  "Mistral":      { "DefaultModel": "mistral-large-latest", "TimeoutSeconds": 90 }
}
```

### Global keys

| Key | Meaning |
|-----|---------|
| `TimeoutSeconds` | Per-call timeout when neither the request, the user's model config, an override nor the provider sets one (default 120). Streams treat it as an idle timeout between deltas. |
| `MaxOutputTokens` | Output cap when nothing more specific sets one. Null omits it (Anthropic then uses 4096, which its API requires). |
| `Temperature` | Temperature when the request and the user's model config set none. Null leaves it to the provider. |
| `ModelConfigDirectory` | Root for per-user `{user}/llm-models.json` files. Empty disables per-user settings. |
| `Retry:MaxAttempts`, `BaseDelaySeconds`, `MaxDelaySeconds`, `UseJitter` | Transient-failure back-off. |
| `Retry:FallbackModels`, `FallbackProviders`, `FallbackIncludesConfiguredModels` | Model-refusal chain; empty `FallbackModels` disables it. |
| `RequestOverrides[]` | `Match`, `MaxOutputTokens`, `TimeoutSeconds`, `RetryOnTimeout`, `ChunkChars`, `ContextLimits{name:chars}`. |

### Per-provider keys (`Llm:<Provider>:…`)

`Endpoint` (alias `BaseUrl`; a full endpoint URL is accepted — known leaves
such as `/chat/completions`, `/responses`, `/v1/messages`, `/api/chat` are
stripped), `ApiKey`, `ApiKeyVariable`, `AuthMode` (`None`, `Bearer`,
`ApiKeyHeader`, `Basic`, `Custom`; the legacy spellings `api-key`, `header`,
`managedidentity`, `entra`, `Auto` are understood), `HeaderName`,
`ApiVersion`, `Deployment` (Azure OpenAI), `TokenScope`, `DefaultModel`,
`Models[]`, `DedicatedModel` and `DedicatedModels[]` (Hugging Face dedicated
endpoints), `Aliases{alias:model}`, `TimeoutSeconds`, `MaxOutputTokens`,
`Username`/`Password` (Basic), `Headers{name:value}`.

### Environment variables

Read before the matching configuration value, in this order:

| Provider | Key | Endpoint | Default model / list | Other |
|----------|-----|----------|----------------------|-------|
| openai | `OPENAI_API_KEY`, `CODEX_API_KEY` | `OPENAI_BASE_URL`, `OPENAI_ENDPOINT`, `CODEX_ENDPOINT` | `OPENAI_MODEL`, `CODEX_MODEL` / `OPENAI_MODELS` | `OPENAI_AUTH_MODE`, `OPENAI_TOKEN_SCOPE` |
| azureopenai | `AZURE_OPENAI_API_KEY` | `AZURE_OPENAI_ENDPOINT` | `AZURE_OPENAI_MODEL`, `AZURE_OPENAI_DEPLOYMENT` / `AZURE_OPENAI_MODELS` | `AZURE_OPENAI_AUTH_MODE`, `AZURE_OPENAI_API_VERSION`, `OPENAI_API_VERSION`, `AZURE_OPENAI_DEPLOYMENT`, `AZURE_OPENAI_TOKEN_SCOPE` |
| azurefoundry | `AZURE_FOUNDRY_API_KEY`, `AZURE_INFERENCE_API_KEY` | `AZURE_FOUNDRY_ENDPOINT`, `AZURE_AI_FOUNDRY_ENDPOINT`, `AZURE_INFERENCE_ENDPOINT` | `AZURE_FOUNDRY_MODEL` / `AZURE_FOUNDRY_MODELS` | `AZURE_FOUNDRY_AUTH_MODE`, `AZURE_FOUNDRY_API_VERSION`, `AZURE_FOUNDRY_TOKEN_SCOPE` |
| anthropic | `ANTHROPIC_API_KEY`, `CLAUDE_API_KEY` | `ANTHROPIC_ENDPOINT`, `CLAUDE_ENDPOINT`, `ANTHROPIC_BASE_URL` | `ANTHROPIC_MODEL`, `CLAUDE_MODEL` / `ANTHROPIC_MODELS`, `CLAUDE_MODELS` | `ANTHROPIC_VERSION` |
| gemini | `GEMINI_API_KEY`, `GOOGLE_API_KEY` | `GEMINI_ENDPOINT` | `GEMINI_MODEL` / `GEMINI_MODELS` | `GEMINI_API_VERSION` |
| ollama | `OLLAMA_API_KEY` | `OLLAMA_ENDPOINT`, `OLLAMA_HOST`, `OLLAMA_ONPREM_ENDPOINT`, `OLLAMA_LOCAL_ENDPOINT` | `OLLAMA_MODEL` / `OLLAMA_MODELS` | `OLLAMA_AUTH_MODE`, `OLLAMA_AUTH_HEADER`, `OLLAMA_USERNAME`, `OLLAMA_PASSWORD` |
| ollamacloud | `OLLAMA_CLOUD_API_KEY` | `OLLAMA_CLOUD_ENDPOINT`, `OLLAMA_REMOTE_ENDPOINT` | `OLLAMA_CLOUD_MODEL` / `OLLAMA_CLOUD_MODELS` | `OLLAMA_CLOUD_AUTH_MODE`, `OLLAMA_CLOUD_AUTH_HEADER`, `OLLAMA_CLOUD_USERNAME`, `OLLAMA_CLOUD_PASSWORD` |
| huggingface | `HUGGINGFACE_API_KEY`, `HF_TOKEN`, `HF_API_KEY` | `HUGGINGFACE_ENDPOINT`, `HF_INFERENCE_ENDPOINT`, `HF_ENDPOINT`, `HF_BASE_URL` | `HUGGINGFACE_MODEL`, `HF_MODEL` / `HUGGINGFACE_MODELS`, `HF_MODELS`; dedicated: `HUGGINGFACE_DEDICATED_MODEL`, `HF_DEDICATED_MODEL` / `HUGGINGFACE_DEDICATED_MODELS`, `HF_DEDICATED_MODELS` | `HUGGINGFACE_AUTH_MODE`, `HF_AUTH_MODE` |
| groq | `GROQ_API_KEY` | `GROQ_ENDPOINT` | `GROQ_MODEL` / `GROQ_MODELS` | |
| xai | `XAI_API_KEY`, `GROK_API_KEY` | `XAI_ENDPOINT`, `GROK_ENDPOINT` | `XAI_MODEL`, `GROK_MODEL` / `XAI_MODELS`, `GROK_MODELS` | |
| mistral | `MISTRAL_API_KEY` | `MISTRAL_API_ENDPOINT`, `MISTRAL_ENDPOINT` | `MISTRAL_API_MODEL`, `MISTRAL_MODEL` / `MISTRAL_API_MODELS`, `MISTRAL_MODELS` | |

Lists split on `,`, `;` and newlines. `ollamacloud` deliberately shares
nothing with `ollama`, so the two daemons can never pick up each other's
credential. A machine set up for GhostWriter needs no new variables.

### Precedence

For every knob a request leaves unset: **request → user's model config →
request override → provider section → `Llm` globals**. Timeouts stop at the
provider/global level; the override's `RetryOnTimeout` retries once.

## Model references

`provider:model` — `anthropic:claude-sonnet-4-6`, `ollama:qwen2.5-coder:32b`,
`huggingface:Qwen/Qwen3-32B:nscale`. A bare id (`gpt-5.4`, `claude-opus-4-7`,
`qwen3:8b`) is resolved through `ModelCatalog.InferProvider`. Rules worth knowing:

- `mistral:7b`, `mistral:latest`, `mistral:7b-instruct-q4_0` are Ollama tags
  (a Mistral AI id never starts with a digit); `mistral:codestral-latest` is
  the Mistral API.
- Ollama family names stand for their default tag: `qwen` → `qwen3:8b`,
  `deepseek` → `deepseek-r1:14b`, `mistral` → `mistral:7b`, `wizardlm` →
  `wizardlm2:7b`, `phi` → `phi3:medium`, `llama` → `llama3.1:8b`, `gemma` →
  `gemma3:4b`. Override with `Llm:Ollama:Aliases`.
- `cloud-ollama:tag` (GhostWriter's picker prefix) routes to `ollamacloud`;
  an `ollama:cloud-ollama:tag` is stripped to the bare tag.
- Anthropic ids are normalised: `claude_sonnet_4.6` → `claude-sonnet-4-6`.
- An `openai:` request goes to `azureopenai` when only Azure is configured,
  and vice versa. When `Llm:OpenAi:Endpoint` (or `OPENAI_BASE_URL`) is an
  Azure host, the `openai` provider itself speaks Azure: `/openai/v1/responses`,
  `api-key` header, bearer token from the `ITokenProvider` when there is no key.

## Examples

### Chat and streaming

```csharp
var result = await client.ChatAsync("anthropic:claude-sonnet-4-6", "Summarise this.", system: "Be brief.");
Console.WriteLine(result.Text);            // result.Usage, result.FinishReason, result.IsTruncated

await foreach (var delta in client.StreamAsync("ollama:qwen2.5-coder:32b", prompt))
    Console.Write(delta.TextDelta);        // the final delta carries Usage + FinishReason
```

### A conversation (Mutarjim's fix loop)

```csharp
var session = client.StartSession("openai:gpt-5.4", system: "You fix C# build errors.",
    new ChatSessionOptions { Temperature = 0, MaxTurns = 20, UserId = user.Id });

var first = await session.AskAsync($"Fix:\n{code}\n\nErrors:\n{errors}");
var second = await session.AskAsync($"Still failing:\n{newErrors}");   // history is sent every time
await foreach (var d in session.AskStreamingAsync("Explain the last change")) Console.Write(d.TextDelta);
session.Reset();                                                         // keeps the system prompt and model
```

`RunToolsAsync` loops tool calls through a handler until the model answers
in text; `Fork()` branches a conversation; `TotalUsage` sums every reply.

### Tools

```csharp
var request = new ChatRequest
{
    Model = "openai:gpt-5.4",
    Messages = new[] { ChatMessage.User("What is the weather in Oslo?") },
    Tools = new[] { new ToolDefinition("weather", "Weather by city", schemaElement) },
    ToolChoice = ToolChoice.Auto,
};
var reply = await client.ChatAsync(request);
if (reply.HasToolCalls)
{
    var call = reply.ToolCalls![0];                       // call.Name, call.ArgumentsJson
    var followUp = request with { Messages = request.Messages
        .Append(ChatMessage.Assistant(new ToolCallPart(call)))
        .Append(ChatMessage.ToolResult(call.Id, "{\"temp\":3}")).ToArray() };
    reply = await client.ChatAsync(followUp);
}
```

### JSON output, with repair for providers that ignore the switch

```csharp
var json = await client.ChatAsync(request with { ResponseFormat = ResponseFormat.JsonSchema, Schema = schema, SchemaName = "analysis" });
if (json.TryParseJson<Analysis>(out var analysis)) { … }

// Any model output: code fences and prose are stripped, a response cut off
// at the token cap is closed, and named fields can be pulled out of prose.
StructuredJson.TryParse(raw, out var document);
var summary = StructuredJson.ExtractLooseString(raw, "synopsis", "summary");
var themes  = StructuredJson.ExtractLooseArray(raw, "themes", limit: 12);
```

`ResponseFormat.Json` maps to each provider's native switch (OpenAI
`json_object`, Gemini `responseMimeType`, Ollama `format`); Anthropic gets a
system-prompt instruction, and `JsonSchema` on Anthropic forces a synthetic
`structured_output` tool whose input is returned as the text.

### Embeddings and images

```csharp
var vectors = await client.EmbedAsync(EmbeddingRequest.For("openai:text-embedding-3-small", "alpha", "beta"));
var image   = await client.GenerateImageAsync(new ImageRequest { Model = "openai:gpt-image-1", Prompt = "a lighthouse at dusk", Size = "1024x1024" });
File.WriteAllBytes("out.png", image.First.Bytes.ToArray());
```

### Listing models and probing providers

```csharp
var models = await client.ListModelsAsync("ollama");          // live where the API has one, configured otherwise
var probe  = await client.ProbeAsync("anthropic");            // Ok / NotConfigured / AuthFailed / Unreachable / Failed
foreach (var p in await client.ProbeAllAsync()) Console.WriteLine($"{p.Provider}: {p.Status} ({p.Elapsed.TotalMilliseconds:0} ms) {p.Message}");
```

The probe is one authenticated call — the model list, or a one-token chat
with the default model for Azure — without retries, capped at 15 s, and never
throws for provider errors.

### Per-user model settings

```csharp
// Llm:ModelConfigDirectory = "App_Data/llm"  →  App_Data/llm/{user}/llm-models.json
var store = sp.GetRequiredService<IModelConfigStore>();
var cfg = store.Get(user.Id, "qwen3:32b");                    // seeded defaults until the user edits
store.Save(user.Id, cfg with { TimeoutSeconds = 300, MaxOutputTokens = 4096, DefaultTemperature = 0.3, Display = false });
var visible = store.GetAll(user.Id).Where(c => c.Display);
```

With a store configured, every `ChatAsync`/`StreamAsync` whose request names
a `UserId` applies that user's `TimeoutSeconds`, `MaxOutputTokens` (0 = no
cap) and `DefaultTemperature` for the model when the request leaves them
unset; the chunk planner reads `ContextTokens`, `ChunkChars` and
`ChunkOnlyWhenTooLarge`. Files written by GhostWriter's store load unchanged.
`ModelConfigSeeds.Default` is the seed table; pass your own to
`UseJsonModelConfigStore(path, seeds)`. A user file that exists but cannot be
read or parsed is logged once per read and answered with the seeds; `Save` and
`Delete` for that user throw instead of overwriting it, and `ResetToDefaults`
is the explicit way to replace it.

### Chunking text that does not fit

```csharp
var planner = sp.GetRequiredService<IChunkPlanner>();
var plan = planner.Plan(new ChunkRequest
{
    Model = "ollama:phi3:medium",
    Text = manuscript,
    FixedPromptChars = systemPrompt.Length + instructions.Length,   // everything sent around the text
    ReservedOutputTokens = 3000,
    UserId = user.Id,
});
var outputs = new List<string>();
foreach (var chunk in plan.Chunks)                 // chunk.PreviousPreview / NextPreview give neighbours
    outputs.Add((await client.ChatAsync(model, BuildPrompt(chunk.Text))).Text);
var whole = planner.Stitch(plan.Chunks, outputs);   // rejoined on the separators it was cut on
```

Fit-or-split: a 1,024-token safety margin under the window, at least 384
tokens per chunk (otherwise `ChunkPlanException`), cuts on paragraphs, then
sentences, then words. `TargetChunkChars` (or a user config with
`ChunkOnlyWhenTooLarge = false`) forces even-sized chunks.

### Request overrides and context trimming

```csharp
var over = sp.GetRequiredService<ILlmRequestOverrides>().Resolve("azurefoundry:phi-4-mini-instruct");
var surrounding = over.Trim("SurroundingContext", surroundingText, keepTail: true);   // null limit = unchanged
```

The client applies an override's `MaxOutputTokens`, `TimeoutSeconds` and
`RetryOnTimeout` itself; `ChunkChars` and `ContextLimits` are for the caller.

### Model fallback

```csharp
llm.UseModelFallback(new ModelFallbackPolicy { Models = new[] { "gpt-5.4", "gpt-4.1" }, Providers = new[] { "openai" } });
```

On a 403/404 whose body says the model is unknown or not enabled for the key
(`LlmHttpException.IsModelAccessError`) the call is repeated with the
provider's configured `Models`, then the policy's list; each observer sees an
`OnRetrying` per hop and `ChatResult.Resolved` names the model that answered.
Chat and streaming calls only — an embedding or image model that is refused
surfaces its error — and the id the provider actually sent (its default when
the request named none) is never tried again. Off unless configured
(`Llm:Retry:FallbackModels` or the builder).

### Call log

```csharp
llm.AddCallRecorder(record => db.LlmCalls.Add(record));   // LlmCallRecord: provider, model, user, usage, cost, elapsed, status, error
var recent = sp.GetRequiredService<InMemoryCallLog>().Recent;
```

Records carry sizes and ids only — never prompt or response text.

## Adding a provider

Derive from `LlmProviderBase` (or implement `ILlmProvider` directly), report
`Capabilities` truthfully, and register it:

```csharp
public sealed class MyProvider : LlmProviderBase
{
    public MyProvider(IHttpClientFactory http, ICredentialResolver credentials) : base(ProviderKeys.Groq, http, credentials) { }
    public override LlmCapabilities Capabilities => LlmCapabilities.Chat | LlmCapabilities.Streaming;
    public override bool IsConfigured => Settings.HasApiKey;
    protected override string? DefaultEndpoint => "https://my.example.com/v1";
    public override async Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken ct) { … }
    public override IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, CancellationToken ct) { … }
}

llm.AddProvider<MyProvider>();     // the last registration for a key wins
```

The base class gives you `Settings` (resolved per call), `ResolveModel`,
`ApplyAuthAsync`, `JsonPost`, `SendForBodyAsync`/`SendForStreamAsync` (error
mapping with `Retry-After`), `Combine`/`WithQuery`/`StripLeaves`, and the
default `ListModelsAsync` that answers with `ConfiguredModels`. Retry, timeout,
model fallback and observation are the client's job, so an adapter is one
HTTP round trip.

## Behaviour

- **Retry** — 429 (honouring `Retry-After`), 502, 503, 504, 529, "overloaded"
  bodies, connection failures, and in-band error events that report the same
  conditions after an HTTP 200 (Anthropic's streamed `overloaded_error` /
  `rate_limit_error`, surfaced as `LlmResponseException.IsTransient`);
  exponential back-off with jitter. A stream is retried only before its first
  delta — an error after text has been yielded is raised, never replayed.
- **Timeout** — request → model config → override → provider → `Llm:TimeoutSeconds`,
  applied through a linked token and raised as `LlmTimeoutException`; the
  caller's own cancellation stays an `OperationCanceledException`.
- **Observers** — `ILlmCallObserver` receives started / completed / failed /
  retrying with model, elapsed, usage, user id and prompt *length* (never content).
- **Capabilities** — `ILlmProvider.Capabilities` says what an adapter really
  does. Azure OpenAI and Azure AI Foundry have no `ListModels` (they answer
  with configured deployments); the Hugging Face router has no embeddings (a
  dedicated endpoint does) and the router alone lists models; Anthropic has no
  embeddings or images. Members outside the set throw `NotSupportedException`,
  except `ListModelsAsync`, which returns `ConfiguredModels`.
- **Usage** — `LlmUsage.Input` excludes cached tokens, which are reported in
  `CacheRead` (and `CacheWrite` for Anthropic).
- **Finish reasons** — normalised to `FinishReasons.Stop` / `MaxTokens` /
  `ToolCalls` / `ContentFilter`; `ChatResult.IsTruncated` flags a token-cap cut.
- **Quirks carried over** — Kimi K2.6 on the Hugging Face router gets
  `thinking: disabled` (it otherwise spends the budget reasoning and returns
  nothing); gpt-5*/o-series/codex models never receive `temperature`; Ollama
  `:cloud` / `-cloud` tags are flagged `Metadata["cloud"]` and given display
  names (`OllamaProvider.DisplayNameFor`).
