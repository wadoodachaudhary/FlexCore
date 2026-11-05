# FlexCore.CodeTools

Blazor pages for the FlexCore code tools. The service lives in `FlexCore.Llm`
(`Fx.ControlKit.Llm.CodeTools`). This package draws the pages and the Mermaid SVG.

```csharp
builder.Services.AddFlexCoreLlm(builder.Configuration);
builder.Services.AddFlexCoreCodeTools(builder.Configuration);
```

```razor
<CodeToolPage Tool="CodeToolKind.Explainer" Service="@Tools" />
<CodeRunnerPage Service="@Tools" />
<CodeChatPanel Service="@Tools" />
```

Code Converter is the existing `ConverterShell`. Do not host a second converter.
The runner calls `SnippetSandbox`, which also returns a `ProgramLaunch` a preview host can start.

See `docs/app-shell-and-code-tools.md`.
