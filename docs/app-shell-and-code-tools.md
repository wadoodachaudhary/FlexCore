# App shell and code tools

Public API for a CodeConvert-style host. Mutarjim should take these types from
FlexCore and render its own pages. It should not copy the components.

Three packages:

| Package | Namespace | What the host uses |
| --- | --- | --- |
| FlexCore | `Fx.ControlKit.Shell` | Sidebar, home, tool menu |
| FlexCore | `Fx.ControlKit.Preview` | Snippet sandbox and `ProgramLaunch` |
| FlexCore | `Fx.ControlKit.Conversion` | The existing `ConverterShell` |
| FlexCore.Llm | `Fx.ControlKit.Llm.CodeTools` | Tool service, prompts, offline diagrams |
| FlexCore.CodeTools | `Fx.ControlKit.CodeTools` | Blazor pages for the tools |

## Shell

`AppShell` is the frame: brand, workspace switcher, sections, a footer, an
optional banner, and the main column. The host owns the active id.

```razor
<AppShell Brand="Mutarjim"
          WorkspaceName="Personal"
          Sections="@sections"
          ActiveItemId="@page"
          ActiveItemIdChanged="id => page = id"
          Theme="ShellTheme.Light"
          Plan="@plan">
    <Banner>…</Banner>
    <ChildContent>…</ChildContent>
</AppShell>
```

`Theme` is `ShellTheme.Light` (the default, green on warm gray) or
`ShellTheme.Dark` (navy, as in the tools menu). Set `Accent` or any
`--fx-shell-*` variable to recolor it. The variables are listed in
`wwwroot/fx-shell.css`.

`SelectAsync`, `SetCollapsedAsync`, and `ToggleGroupAsync` are public so a host
or a test can drive the sidebar. A disabled wizard step does not become active.

### Wizard steps

A migration flow is a parent item whose children carry `ShellWizardStep`
(`Number` and `State`: `Pending`, `Active`, `Done`, `Disabled`).

```csharp
var wizard = ShellToolCatalog.ConverterWithSteps(
    ShellWizard.Step(1, "upload", "Upload", ShellStepState.Done),
    ShellWizard.Step(2, "inventory", "Inventory", ShellStepState.Active),
    ShellWizard.Step(3, "convert", "Convert"),
    ShellWizard.Step(4, "todo", "TODO", ShellStepState.Disabled),
    ShellWizard.Step(5, "run", "Run"));
var sections = ShellToolCatalog.CreateSidebar(wizard);
```

Choosing a step raises `OnNavigate` with `ShellNavigation(Id, ParentId, StepNumber)`.
`ConverterWithSteps` keeps Code Converter selectable and shows the steps under it.
`ShellWizard.Group` is the same shape when the parent itself is not a page.

### Tool catalog

`ShellToolCatalog.Menu` is the one list for the card grid and the sidebar.
Each `ShellToolCard` has `Id`, `Title`, `Description`, `Icon`, `Color`, and
`Route` (a path segment, not a URL).

Order matches the tools menu, then Comment Generator:

1. generator, assistant, explainer, enhancer
2. converter, documentation, unit-tests, reviewer
3. optimizer, runner, diagram, diagram-to-code
4. chat, comments

```razor
<ShellToolGrid Tools="@ShellToolCatalog.Menu" ActiveId="@page" OnSelect="Open" />
```

`ShellToolCatalog.ToNavItems` and `CreateSidebar` build the sidebar from that
same list. `GetStarted` is the three home cards (Converter, Explainer, Generator).

`ShellHome` renders those cards plus pinned and popular tiles. A tile with
`SourceLanguageId` and `TargetLanguageId` draws two `ShellLanguageMark`s and an
arrow. Ids match `CodeLanguages` (`python`, `c`, `csharp`, `ruby`, …).

## Code tools service

```csharp
builder.Services.AddFlexCoreLlm(builder.Configuration);
builder.Services.AddFlexCoreCodeTools(builder.Configuration);
```

```json
"Llm": {
  "CodeTools": { "Model": "openai:gpt-5.4", "AllowImplicitOllama": false }
}
```

`ICodeToolService` is the type to inject.

| Member | Behavior |
| --- | --- |
| `GetAvailability(model?)` | `IsConfigured`, provider, model, streaming, vision, and a message |
| `RunAsync(request)` | One tool call. Never throws for a missing model |
| `StreamAsync(request)` | Text deltas, then a final `CodeToolResult` |
| `OpenChat(model?)` | `ICodeChat` whose `Turns` stay on that instance |

`CodeToolRequest` fields: `Tool`, `Code`, `Files`, `Instruction`, `Language`,
`TargetLanguage`, `Diagram` (`Class`, `Sequence`, `Flow`), `Mermaid`, `Image`,
`Model`, `PreferOffline`.

`CodeToolResult` fields: `Status`, `Text`, `Code`, `Mermaid`, `Summary`,
`Explanation`, `Suggestions`, `Findings`, `FileName`, `SetupMessage`, `Error`,
`UsedOfflineFallback`, `Model`.

`CodeToolStatus`: `Succeeded`, `NotConfigured`, `Failed`, `OfflineFallback`,
`NeedsVision`.

`CodeToolCatalog.Get(kind)` describes inputs (`NeedsCode`, `NeedsInstruction`,
`NeedsTargetLanguage`, `NeedsFiles`, `AcceptsImage`) and the presentation
(`Markdown`, `Code`, `Diagram`, `Suggestions`, `Findings`, `Documentation`,
`Optimized`, `Chat`). `CodeToolMap.TryGetKind(id)` maps a shell id onto a kind.
`converter` and `runner` are not model calls.

`CodeToolPrompts.SystemPrompt` and `UserPrompt` are the text the service sends.
Comment Generator and Documentation Generator both use `CommentRules`, so a
Python request names PEP 257 in both. Unit tests name the framework on
`CodeLanguage`: xUnit, pytest, Jest, GoogleTest, and the others in
`CodeLanguages.All`.

### Structured results

- Enhancer: `{ summary, suggestions: [{ title, before, after, rationale }] }`
- Reviewer: `{ summary, findings: [{ severity, line, message, recommendation }] }`
  sorted high, then medium, then low
- Optimizer: `{ explanation, code }`
- Diagram: `{ summary, mermaid }`

### Offline class diagrams

`StructuralClassDiagram.TryCreate(language, code, out mermaid)` reads C# or
Python (`c#`, `csharp`, `python`, `py`) and returns a `classDiagram`. The same
source always returns the same text. Other languages return false.

`RunAsync` uses that diagram when `PreferOffline` is set, when no model is
configured, or when a model reply has no Mermaid. `Status` is `OfflineFallback`.

### Diagram to code and vision

Send `Mermaid` text, or a `CodeToolImage` (`Bytes`, `MimeType`). An image sent
to a model whose catalog entry or provider capability lacks
`LlmCapabilities.Vision` returns `NeedsVision` and
`CodeToolMessages.VisionMessage` without calling the model.

### When no model is configured

`GetAvailability().IsConfigured` is false and `Message` is
`CodeToolMessages.SetupMessage`. `RunAsync` returns `NotConfigured` with the
same text and does not call `ILlmClient`. The default Ollama localhost URL does
not count as configuration.

## Blazor pages

`FlexCore.CodeTools` references FlexCore and FlexCore.Llm.

```razor
<CodeToolPage Tool="CodeToolKind.Explainer" Service="@Tools" Code="@code" AutoRun="true" />
<CodeRunnerPage Service="@Tools" Language="python" Code="@snippet" />
<CodeChatPanel Service="@Tools" />
```

`CodeToolPage` is the dual pane: language and code on the left, the result on
the right, with Copy and a `data:` download link. Explanations render as
Markdown (raw HTML in the model text is escaped). Diagrams render as SVG from
`MermaidDiagram.TryRenderSvg` and keep the Mermaid source. Link
`_content/FlexCore.CodeTools/code-tools.css` if the page is not picking up the
scoped bundle.

Code Converter is `ConverterShell`. Do not add a second converter. The catalog
route `converter` is the place to host it.

## Snippet sandbox

`SnippetSandbox` runs untrusted C#, Python, or JavaScript in a subprocess:

- a private temp directory, deleted when the run finishes
- an allow-listed environment (`PATH`, locale, `DOTNET_ROOT`); secrets in the
  parent process are not copied
- `ulimit` for CPU seconds, file size, process count, and open files on Linux
- a wall-clock timeout (1–60 seconds) and an output cap
- `dotnet`, `python3`, or `node`, whichever is on `PATH`

```csharp
var result = await SnippetSandbox.RunAsync(new SnippetRunRequest
{
    Language = "python",
    Code = "print(1)\n",
    Timeout = TimeSpan.FromSeconds(8)
});
```

`result.Status` is `Succeeded`, `Failed`, `TimedOut`, `RuntimeMissing`, or
`Rejected`. A missing runtime uses `SnippetSandbox.PythonMissing`,
`JavaScriptMissing`, or `CSharpMissing`.

`Prepare` writes the files and returns a `ProgramLaunch` of kind `Console`.
`IProgramPreviewHost.StartAsync` can start that launch for a live console.
`RunAsync` starts the same launch and is the path that applies the limits.
This is a constrained subprocess, not a virtual machine. A multi-tenant host
that must contain hostile code should put it in a container or under a
separate user.

`CodeRunnerPage` calls `RunAsync`. On `Failed` or `TimedOut` it offers
**Explain this error**, which calls `CodeToolKind.Assistant` with the stderr.

C# snippets are a `dotnet run` project, so give them a longer `Timeout`
(the page uses 45 seconds). Python and JavaScript default to 8 seconds on the page.

## Sample host

`tests/FlexCore.ShellSample` renders the shell, the home tiles, the tool grid,
the wizard steps, the explainer, and the diagram. Its `SampleLlmClient` is a
stand-in so the pages render without an API key. Replace it with
`AddFlexCoreLlm` for a real provider.
