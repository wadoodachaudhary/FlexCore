using System.Text;
using Fx.ControlKit.Llm.CodeTools;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace Fx.ControlKit.CodeTools;

/// <summary>
/// Dual-pane page for one code tool: input on the left, the result on the right,
/// with copy, download, Markdown, and Mermaid. Pass <see cref="Service"/> from
/// <c>AddFlexCoreCodeTools</c>. A missing model shows setup text instead of throwing.
/// </summary>
public partial class CodeToolPage
{
    private readonly List<FileSlot> _files = [];
    private readonly StringBuilder _stream = new();
    private CodeToolDescriptor? _descriptor;
    private CodeToolAvailability? _availability;
    private CodeToolResult? _result;
    private CodeToolImage? _image;
    private string _code = "";
    private string _instruction = "";
    private string _language = CodeLanguages.CSharp.Id;
    private string _target = CodeLanguages.Python.Id;
    private string _mermaid = "";
    private DiagramKind _diagram = DiagramKind.Class;
    private string? _setup;
    private string? _error;
    private string _copyLabel = "Copy";
    private bool _seeded;
    private bool _autoRan;

    [Parameter] public CodeToolKind Tool { get; set; } = CodeToolKind.Explainer;
    [Parameter] public ICodeToolService? Service { get; set; }
    [Parameter] public string? Model { get; set; }
    [Parameter] public string? Code { get; set; }
    [Parameter] public string? Instruction { get; set; }
    [Parameter] public string? Language { get; set; }
    [Parameter] public string? TargetLanguage { get; set; }
    [Parameter] public DiagramKind Diagram { get; set; } = DiagramKind.Class;
    [Parameter] public string? Mermaid { get; set; }
    [Parameter] public IReadOnlyList<CodeToolFile>? Files { get; set; }
    [Parameter] public bool AutoRun { get; set; }
    [Parameter] public bool PreferOffline { get; set; }
    [Parameter] public bool ShowHeader { get; set; } = true;
    [Parameter] public string? CssClass { get; set; }

    private CodeToolDescriptor Descriptor => _descriptor ?? CodeToolCatalog.Get(Tool);
    private bool Busy { get; set; }
    private bool ShowSetup => !string.IsNullOrEmpty(_setup) && (_result is null || _result.Status == CodeToolStatus.NotConfigured);

    private string ResultLabel => _result?.Status switch
    {
        CodeToolStatus.OfflineFallback => "Diagram",
        CodeToolStatus.NotConfigured => "Setup",
        _ => "Result"
    };

    private string? DownloadText => _result switch
    {
        { Mermaid: { Length: > 0 } mermaid } => mermaid,
        { Code: { Length: > 0 } code } => code,
        { Text: { Length: > 0 } text } when _result.Status is CodeToolStatus.Succeeded or CodeToolStatus.OfflineFallback => text,
        _ => null
    };

    private string DownloadName => string.IsNullOrWhiteSpace(_result?.FileName) ? "result.txt" : _result.FileName;
    private string DownloadHref => "data:text/plain;charset=utf-8," + Uri.EscapeDataString(DownloadText ?? "");

    protected override async Task OnParametersSetAsync()
    {
        _descriptor = CodeToolCatalog.Get(Tool);
        if (!_seeded)
        {
            _seeded = true;
            _code = Code ?? "";
            _instruction = Instruction ?? "";
            _language = CodeLanguages.FindOrDefault(Language).Id;
            _target = CodeLanguages.FindOrDefault(string.IsNullOrWhiteSpace(TargetLanguage) ? Language : TargetLanguage).Id;
            _diagram = Diagram;
            _mermaid = Mermaid ?? "";
            if (Files is { Count: > 0 })
            {
                foreach (var file in Files)
                    _files.Add(new FileSlot { Name = file.Name, Content = file.Content });
            }
            else if (Descriptor.NeedsFiles)
            {
                _files.Add(new FileSlot { Name = "Program" + CodeLanguages.FindOrDefault(_language).FileExtension, Content = _code });
            }
        }

        RefreshAvailability();
        if (AutoRun && !_autoRan && Service is not null)
        {
            _autoRan = true;
            await RunAsync();
        }
    }

    /// <summary>Sends the current input to <see cref="Service"/>.</summary>
    public async Task RunAsync()
    {
        _error = null;
        _result = null;
        _stream.Clear();
        _copyLabel = "Copy";
        if (Service is null)
        {
            _setup = "Register ICodeToolService with AddFlexCoreCodeTools, or pass Service to this page.";
            await InvokeAsync(StateHasChanged);
            return;
        }

        Busy = true;
        var request = BuildRequest();
        try
        {
            var stream = Descriptor.Presentation is CodeToolPresentation.Markdown or CodeToolPresentation.Code or CodeToolPresentation.Documentation;
            if (stream)
            {
                await foreach (var delta in Service.StreamAsync(request))
                {
                    if (delta.TextDelta is { } text) _stream.Append(text);
                    if (delta.Final is { } final) _result = final;
                    await InvokeAsync(StateHasChanged);
                }
            }
            else
            {
                _result = await Service.RunAsync(request);
            }

            if (_result?.Status == CodeToolStatus.NotConfigured)
                _setup = _result.SetupMessage;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            Busy = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private CodeToolRequest BuildRequest() => new()
    {
        Tool = Tool,
        Code = _code,
        Instruction = _instruction,
        Language = _language,
        TargetLanguage = _target,
        Diagram = _diagram,
        Mermaid = _mermaid,
        Image = _image,
        Model = Model,
        PreferOffline = PreferOffline,
        Files = _files.Where(file => !string.IsNullOrWhiteSpace(file.Content))
            .Select(file => new CodeToolFile(string.IsNullOrWhiteSpace(file.Name) ? "file" : file.Name.Trim(), file.Content))
            .ToList()
    };

    private void RefreshAvailability()
    {
        if (Service is null)
        {
            _setup = "Register ICodeToolService with AddFlexCoreCodeTools, or pass Service to this page.";
            return;
        }

        _availability = Service.GetAvailability(Model);
        _setup = _availability.IsConfigured ? null : _availability.Message;
    }

    private async Task CopyAsync()
    {
        var text = DownloadText ?? _result?.Text ?? _stream.ToString();
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            var module = await Js.InvokeAsync<IJSObjectReference>("import", "./_content/FlexCore.CodeTools/code-tool.js");
            var copied = await module.InvokeAsync<bool>("copyText", text);
            _copyLabel = copied ? "Copied" : "Copy";
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or JSDisconnectedException)
        {
            _copyLabel = "Copy";
        }
    }

    private async Task OnImageAsync(InputFileChangeEventArgs change)
    {
        var file = change.File;
        if (file.Size > 4 * 1024 * 1024)
        {
            _error = "Use an image up to 4 MB.";
            return;
        }

        await using var stream = file.OpenReadStream(4 * 1024 * 1024);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        var mime = string.IsNullOrWhiteSpace(file.ContentType) ? "image/png" : file.ContentType;
        _image = new CodeToolImage(buffer.ToArray(), mime);
    }

    private void OnLanguage(string? value)
    {
        if (!string.IsNullOrEmpty(value)) _language = value;
    }

    private void OnTarget(string? value)
    {
        if (!string.IsNullOrEmpty(value)) _target = value;
    }

    private void OnDiagram(string? value)
    {
        if (Enum.TryParse<DiagramKind>(value, out var kind)) _diagram = kind;
    }

    private void OnCode(string? value) => _code = value ?? "";

    private void OnInstruction(string? value) => _instruction = value ?? "";

    private void OnMermaid(string? value) => _mermaid = value ?? "";

    private void SetFileName(int index, string? name)
    {
        if (index >= 0 && index < _files.Count) _files[index].Name = name ?? "";
    }

    private void SetFileContent(int index, string? content)
    {
        if (index >= 0 && index < _files.Count) _files[index].Content = content ?? "";
    }

    private void AddFile() => _files.Add(new FileSlot { Name = "File" + (_files.Count + 1) + ".txt", Content = "" });

    private void RemoveFile(int index)
    {
        if (index >= 0 && index < _files.Count) _files.RemoveAt(index);
    }

    private sealed class FileSlot
    {
        public string Name { get; set; } = "";
        public string Content { get; set; } = "";
    }
}
