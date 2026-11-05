using System.Runtime.CompilerServices;
using System.Text;
using Fx.ControlKit.CodeTools;
using Fx.ControlKit.Llm;
using Fx.ControlKit.Llm.CodeTools;
using Fx.ControlKit.Llm.Configuration;
using Fx.ControlKit.Shell;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

var checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}

var order = StructuralClassDiagram.TryCreate("c#", Samples.CSharp, out var mermaid);
Check(order && mermaid.Contains("class Order") && mermaid.Contains("class OrderLine") && mermaid.Contains("Order --> OrderLine"), "C# structure becomes a class diagram");
var again = StructuralClassDiagram.TryCreate("csharp", Samples.CSharp, out var mermaidAgain);
Check(again && mermaidAgain == mermaid, "the class diagram is deterministic");
Check(StructuralClassDiagram.TryCreate("python", Samples.Python, out var python) && python.Contains("class Order") && python.Contains("Order --|> Model") && python.Contains("+total()"), "Python classes, bases, and methods are drawn");
Check(!StructuralClassDiagram.TryCreate("java", "class A {}", out _), "Java has no offline diagram");

Check(CodeToolPrompts.SystemPrompt(new CodeToolRequest { Tool = CodeToolKind.UnitTestGenerator, Language = "csharp" }).Contains("xUnit"), "C# tests ask for xUnit");
Check(CodeToolPrompts.SystemPrompt(new CodeToolRequest { Tool = CodeToolKind.UnitTestGenerator, Language = "python" }).Contains("pytest"), "Python tests ask for pytest");
Check(CodeToolPrompts.SystemPrompt(new CodeToolRequest { Tool = CodeToolKind.UnitTestGenerator, Language = "javascript" }).Contains("Jest"), "JavaScript tests ask for Jest");
Check(CodeToolPrompts.SystemPrompt(new CodeToolRequest { Tool = CodeToolKind.UnitTestGenerator, Language = "cpp" }).Contains("GoogleTest"), "C++ tests ask for GoogleTest");
var comments = CodeToolPrompts.SystemPrompt(new CodeToolRequest { Tool = CodeToolKind.CommentGenerator, Language = "python" });
var docs = CodeToolPrompts.SystemPrompt(new CodeToolRequest { Tool = CodeToolKind.DocumentationGenerator, Language = "python" });
Check(comments.Contains("PEP 257") && docs.Contains("PEP 257"), "comments and documentation share the language idiom");
Check(CodeToolPrompts.UserPrompt(new CodeToolRequest { Tool = CodeToolKind.DocumentationGenerator, Files = [new CodeToolFile("Order.cs", "class Order {}"), new CodeToolFile("Line.cs", "class Line {}")] }).Contains("Order.cs") && CodeToolPrompts.UserPrompt(new CodeToolRequest { Tool = CodeToolKind.DocumentationGenerator, Files = [new CodeToolFile("Order.cs", "x")] }).Contains("Line.cs") == false, "documentation prompt names each file");
Check(CodeToolPrompts.SystemPrompt(new CodeToolRequest { Tool = CodeToolKind.Reviewer }).Contains("severity"), "the reviewer prompt asks for severity");

var emptyClient = new FakeLlm();
var none = new CodeToolService(emptyClient);
var missing = await none.RunAsync(new CodeToolRequest { Tool = CodeToolKind.Explainer, Code = "x", Language = "csharp" });
Check(missing.Status == CodeToolStatus.NotConfigured && missing.SetupMessage == CodeToolMessages.SetupMessage && emptyClient.Requests.Count == 0, "an unconfigured model returns setup text and does not call");
var ollamaOnly = new CodeToolService(new FakeLlm(new FakeProvider("ollama")));
Check(!ollamaOnly.GetAvailability().IsConfigured, "implicit local Ollama is not treated as configured");
var allowed = new CodeToolService(new FakeLlm(new FakeProvider("ollama")), options: new CodeToolOptions { AllowImplicitOllama = true, Model = "ollama:qwen" });
Check(allowed.GetAvailability().IsConfigured, "AllowImplicitOllama opts in to the local daemon");

var fake = new FakeLlm(new FakeProvider("openai", LlmCapabilities.Chat | LlmCapabilities.Streaming | LlmCapabilities.Vision));
var tools = new CodeToolService(fake, new FixedCredentials(), new CodeToolOptions { Model = "openai:sample" });
var explained = await tools.RunAsync(new CodeToolRequest { Tool = CodeToolKind.Explainer, Code = "print(1)", Language = "python" });
Check(explained.Status == CodeToolStatus.Succeeded && explained.Text.Contains("prints"), "the explainer returns the model text");
Check(fake.Requests[0].System!.Contains("Tool: Explainer") && fake.Requests[0].Messages[0].Text.Contains("print(1)"), "the explainer prompt includes the code");

var tests = await tools.RunAsync(new CodeToolRequest { Tool = CodeToolKind.UnitTestGenerator, Code = "def add(a, b): return a + b", Language = "python" });
Check(tests.Code!.Contains("def test_add") && tests.FileName == "Tests.py", "generated tests keep the fenced code");

var review = await tools.RunAsync(new CodeToolRequest { Tool = CodeToolKind.Reviewer, Code = "eval(x)", Language = "python" });
Check(review.Findings.Count == 2 && review.Findings[0].Severity == "high" && review.Findings[0].Line == 4 && review.Findings[1].Severity == "low", "findings are ranked by severity");

var optimized = await tools.RunAsync(new CodeToolRequest { Tool = CodeToolKind.Optimizer, Code = "def slow():\n    pass", Language = "python" });
Check(optimized.Code == "def fast():\n    return 1" && optimized.Explanation!.Contains("one pass"), "the optimizer returns code and an explanation");

var documented = await tools.RunAsync(new CodeToolRequest
{
    Tool = CodeToolKind.DocumentationGenerator,
    Language = "csharp",
    Files = [new CodeToolFile("Order.cs", "public class Order {}")]
});
Check(documented.Status == CodeToolStatus.Succeeded && documented.Text.Contains("# Order") && documented.FileName == "README.md", "documentation comes back as Markdown");

var offline = await none.RunAsync(new CodeToolRequest { Tool = CodeToolKind.DiagramGenerator, Diagram = DiagramKind.Class, Language = "csharp", Code = Samples.CSharp });
Check(offline.Status == CodeToolStatus.OfflineFallback && offline.Mermaid!.Contains("class Order") && offline.UsedOfflineFallback, "a class diagram falls back without a model");
var beforeOffline = fake.Requests.Count;
var preferred = await tools.RunAsync(new CodeToolRequest { Tool = CodeToolKind.DiagramGenerator, Diagram = DiagramKind.Class, Language = "csharp", Code = Samples.CSharp, PreferOffline = true });
Check(preferred.UsedOfflineFallback && fake.Requests.Count == beforeOffline, "prefer offline skips the model");

var drawn = await tools.RunAsync(new CodeToolRequest { Tool = CodeToolKind.DiagramGenerator, Diagram = DiagramKind.Class, Language = "java", Code = "class A {}" });
Check(drawn.Mermaid!.Contains("classDiagram") && drawn.Summary!.Contains("Order"), "a model diagram is parsed from JSON");

var blind = new CodeToolService(new FakeLlm(new FakeProvider("openai", LlmCapabilities.Chat)), options: new CodeToolOptions { Model = "openai:sample" });
var image = await blind.RunAsync(new CodeToolRequest { Tool = CodeToolKind.DiagramToCode, TargetLanguage = "csharp", Image = new CodeToolImage([1, 2, 3], "image/png") });
Check(image.Status == CodeToolStatus.NeedsVision && image.Error == CodeToolMessages.VisionMessage, "an image without vision explains how to proceed");
var seen = await tools.RunAsync(new CodeToolRequest { Tool = CodeToolKind.DiagramToCode, TargetLanguage = "python", Mermaid = "flowchart TD\n  A-->B", Image = new CodeToolImage([9], "image/png") });
Check(seen.Code!.Contains("def build") && fake.Requests[^1].Messages[0].Parts.OfType<ImagePart>().Any(), "diagram-to-code sends the image and returns code");

var streamed = new List<string>();
await foreach (var delta in tools.StreamAsync(new CodeToolRequest { Tool = CodeToolKind.Explainer, Code = "x = 1", Language = "python" }))
{
    if (delta.TextDelta is not null) streamed.Add(delta.TextDelta);
    if (delta.Final is not null) Check(delta.Final.Text.Contains("prints"), "the streamed explainer finishes with the full text");
}
Check(string.Concat(streamed).Contains("prints"), "streaming yields the explanation");

var chat = tools.OpenChat();
var first = await chat.SendAsync("How do I reverse a list?");
var second = await chat.SendAsync("And in Python?");
Check(first.Text.Contains("reverse") && chat.Turns.Count == 4, "chat keeps the session history");
Check(fake.Requests[^1].Messages.Count >= 3, "the second chat turn sends the earlier messages");
_ = second;

var html = MarkdownRender.ToHtml("## Title\n\nSee `code` and **bold**.\n\n<script>alert(1)</script>\n");
Check(html.Contains("<h2>Title</h2>") && html.Contains("<code>code</code>") && html.Contains("&lt;script&gt;"), "Markdown renders headings and escapes HTML");
Check(MermaidDiagram.TryRenderSvg(mermaid, out var svg) && svg.Contains("<svg") && svg.Contains("Order") && svg.Contains("OrderLine"), "the class diagram renders to SVG");
Check(MermaidDiagram.TryRenderSvg("sequenceDiagram\nparticipant A\nparticipant B\nA->>B: Hello\n", out var sequence) && sequence.Contains("Hello"), "a sequence diagram renders");
Check(MermaidDiagram.TryRenderSvg("flowchart TD\nA[Start] --> B{Ready}\n", out var flow) && flow.Contains("Start") && flow.Contains("Ready"), "a flowchart renders");

var shellIds = ShellToolCatalog.Menu.Select(card => card.Id).ToHashSet(StringComparer.Ordinal);
foreach (var descriptor in CodeToolCatalog.All)
    Check(shellIds.Contains(descriptor.Id), "shell catalog includes " + descriptor.Id);
Check(CodeToolMap.TryGetKind("runner", out _) == false && CodeToolMap.TryGetKind("converter", out _) == false, "runner and converter are not model calls");
Check(CodeToolMap.TryGetKind("reviewer", out var reviewerKind) && reviewerKind == CodeToolKind.Reviewer, "reviewer maps onto the service tool");

var services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime, NoJs>().BuildServiceProvider();
await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
var activator = new Components();
services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime, NoJs>().AddSingleton<IComponentActivator>(activator).BuildServiceProvider();
await renderer.DisposeAsync();
await using var ui = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
await ui.Dispatcher.InvokeAsync(async () =>
{
    var unconfigured = await ui.RenderComponentAsync<CodeToolPage>(ParameterView.FromDictionary(new Dictionary<string, object?>
    {
        ["Tool"] = CodeToolKind.Explainer,
        ["Service"] = new CodeToolService(new FakeLlm()),
        ["Code"] = "print(1)"
    }));
    var setupHtml = unconfigured.ToHtmlString();
    Check(setupHtml.Contains("Model setup") && setupHtml.Contains("No language model is configured") && setupHtml.Contains("OPENAI_API_KEY"), "the explainer page shows setup text");

    var pageRoot = await ui.RenderComponentAsync<CodeToolPage>(ParameterView.FromDictionary(new Dictionary<string, object?>
    {
        ["Tool"] = CodeToolKind.Explainer,
        ["Service"] = tools,
        ["Code"] = "print(1)",
        ["Language"] = "python",
        ["AutoRun"] = true
    }));
    var pageHtml = pageRoot.ToHtmlString();
    Check(pageHtml.Contains("<h2>What it does</h2>") && !pageHtml.Contains("<script>"), "the explainer page renders Markdown");
    Check(pageHtml.Contains(">Copy<") && pageHtml.Contains("download=\"explanation.md\"") && pageHtml.Contains("data:text/plain"), "copy and download are on the result");

    var diagramPage = await ui.RenderComponentAsync<CodeToolPage>(ParameterView.FromDictionary(new Dictionary<string, object?>
    {
        ["Tool"] = CodeToolKind.DiagramGenerator,
        ["Service"] = none,
        ["Code"] = Samples.CSharp,
        ["Language"] = "csharp",
        ["PreferOffline"] = true,
        ["AutoRun"] = true
    }));
    var diagramHtml = diagramPage.ToHtmlString();
    Check(diagramHtml.Contains("<svg") && diagramHtml.Contains("OrderLine") && diagramHtml.Contains("Mermaid source"), "the diagram page draws the offline SVG");

    var reviewPage = await ui.RenderComponentAsync<CodeToolPage>(ParameterView.FromDictionary(new Dictionary<string, object?>
    {
        ["Tool"] = CodeToolKind.Reviewer,
        ["Service"] = tools,
        ["Code"] = "eval(x)",
        ["AutoRun"] = true
    }));
    Check(reviewPage.ToHtmlString().Contains("data-severity=\"high\"") && reviewPage.ToHtmlString().Contains("Line 4"), "the reviewer page lists ranked findings");
});

Console.WriteLine($"All {checks} code tool checks passed.");

static class Samples
{
    public const string CSharp = """
    public class Order
    {
        public int Id { get; set; }
        public string Customer { get; set; } = "";
        public List<OrderLine> Lines { get; set; } = new();
        public decimal Total() => 0;
    }

    public class OrderLine
    {
        public string Sku { get; set; } = "";
        public int Quantity { get; set; }
    }
    """;

    public const string Python = """
    class Order(Model):
        def __init__(self, customer):
            self.customer = customer
            self.lines = []

        def total(self):
            return 0

    class OrderLine:
        def __init__(self, sku, quantity):
            self.sku = sku
            self.quantity = quantity
    """;
}

sealed class FakeLlm : ILlmClient
{
    public FakeLlm(params FakeProvider[] providers) => Providers = providers;
    public List<ChatRequest> Requests { get; } = [];
    public IReadOnlyList<ILlmProvider> Providers { get; }
    public ILlmProvider Resolve(string providerKey) => Providers.First(provider => provider.Key == providerKey);
    public ILlmProvider Resolve(ModelRef model) => Resolve(model.Provider);
    public bool TryResolve(string providerKey, out ILlmProvider? provider)
    {
        provider = Providers.FirstOrDefault(item => item.Key == providerKey);
        return provider is not null;
    }

    public Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Task.FromResult(Reply(request));
    }

    public async IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var text = Reply(request).Text;
        Requests.Add(request);
        foreach (var chunk in Chunk(text)) yield return ChatDelta.Text(chunk);
        yield return ChatDelta.Final(null, FinishReasons.Stop, resolved: request.Model);
        await Task.CompletedTask;
    }

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(string providerKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModelInfo>>([new ModelInfo(providerKey, "sample")]);
    public Task<ImageResult> GenerateImageAsync(ImageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ProviderProbe> ProbeAsync(string providerKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProviderProbe(providerKey, ProbeStatus.Ok, true, TimeSpan.Zero));

    private static ChatResult Reply(ChatRequest request)
    {
        var system = request.System ?? "";
        var text = system switch
        {
            _ when system.Contains("Tool: Explainer") => "## What it does\n\nIt prints a value.",
            _ when system.Contains("Tool: UnitTestGenerator") => "```python\ndef test_add():\n    assert add(1, 2) == 3\n```",
            _ when system.Contains("Tool: Reviewer") => """{"summary":"Two issues.","findings":[{"severity":"low","line":1,"message":"Name is vague.","recommendation":"Rename it."},{"severity":"high","line":4,"message":"eval executes untrusted text.","recommendation":"Parse the input instead."}]}""",
            _ when system.Contains("Tool: Optimizer") => """{"explanation":"Use one pass.","code":"def fast():\n    return 1"}""",
            _ when system.Contains("Tool: DocumentationGenerator") => "# Order\n\nA small order type.\n\n## API\n\n- Order",
            _ when system.Contains("Tool: DiagramGenerator") => """{"summary":"Order has lines.","mermaid":"classDiagram\n    class Order {\n        +int Id\n    }\n    class OrderLine {\n        +string Sku\n    }\n    Order --> OrderLine : Lines"}""",
            _ when system.Contains("Tool: DiagramToCode") => "```python\ndef build():\n    return []\n```",
            _ when system.Contains("Tool: Chat") => request.Messages[^1].Text.Contains("Python", StringComparison.Ordinal) ? "Use reversed(items)." : "Call reverse on the list.",
            _ => "ok"
        };
        return new ChatResult(text, request.Model, null, FinishReasons.Stop, null, TimeSpan.Zero, null);
    }

    private static IEnumerable<string> Chunk(string text)
    {
        for (var i = 0; i < text.Length; i += 12)
            yield return text.Substring(i, Math.Min(12, text.Length - i));
    }
}

sealed class FakeProvider : ILlmProvider
{
    public FakeProvider(string key, LlmCapabilities capabilities = LlmCapabilities.Chat | LlmCapabilities.Streaming)
    {
        Key = key;
        Capabilities = capabilities;
    }

    public string Key { get; }
    public LlmCapabilities Capabilities { get; }
    public bool IsConfigured => true;
    public IReadOnlyList<ModelInfo> ConfiguredModels => [new ModelInfo(Key, "sample")];
    public Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken) => Task.FromResult(ConfiguredModels);
    public Task<ImageResult> GenerateImageAsync(ImageRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
}

sealed class FixedCredentials : ICredentialResolver
{
    public ProviderSettings Resolve(string providerKey) => new(providerKey, null, "test-key", null, null, null, null, null, "sample", [], null, null, null, null, new Dictionary<string, string>());
}

sealed class Components : IComponentActivator
{
    public IComponent CreateInstance(Type type) => (IComponent)Activator.CreateInstance(type)!;
}

sealed class NoJs : IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
}
