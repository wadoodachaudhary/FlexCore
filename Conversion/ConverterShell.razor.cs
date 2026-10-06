using System.Text;
using Fx.ControlKit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Fx.ControlKit.Conversion;

/// <summary>
/// Dual-pane source and target converter shell. The host supplies kind lists and
/// implements <see cref="IAsyncCallback{TArgument, TResult}"/> for
/// <see cref="ConverterRequest"/> / <see cref="ConverterResult"/>. This control
/// does not embed a language or format catalog.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ConverterShellMode.SingleFile"/> shows one source editor and one
/// output editor. Upload replaces that source. <see cref="ConverterShellMode.FileBrowser"/>
/// shows a source file list beside the selected file and a converted file list
/// beside the selected output.
/// </para>
/// <para>
/// With <see cref="AutoMode"/> left on, any bound source file — or more than one
/// converted file — selects the file browser until the user or host picks a mode.
/// Passing <see cref="Mode"/> as <see cref="ConverterShellMode.FileBrowser"/> also
/// pins that layout. Set <see cref="AutoMode"/> to false to follow <see cref="Mode"/>
/// exactly, including a later return to automatic layout when it is set back to true.
/// </para>
/// </remarks>
public partial class ConverterShell
{
    private static readonly IReadOnlyList<SelectionItem> ModeItems =
    [
        new(nameof(ConverterShellMode.SingleFile), "Single file"),
        new(nameof(ConverterShellMode.FileBrowser), "File browser")
    ];

    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Parameter] public IReadOnlyList<ConverterKind> SourceKinds { get; set; } = [];
    [Parameter] public string? SourceKindId { get; set; }
    [Parameter] public EventCallback<string?> SourceKindIdChanged { get; set; }
    [Parameter] public IReadOnlyList<ConverterKind> TargetKinds { get; set; } = [];
    [Parameter] public string? TargetKindId { get; set; }
    [Parameter] public EventCallback<string?> TargetKindIdChanged { get; set; }
    [Parameter] public RenderFragment<ConverterKind?>? SourceKindAdornment { get; set; }
    [Parameter] public RenderFragment<ConverterKind?>? TargetKindAdornment { get; set; }
    [Parameter] public string SourceKindPlaceholder { get; set; } = "Source";
    [Parameter] public string TargetKindPlaceholder { get; set; } = "Target";
    [Parameter] public string SourceKindAriaLabel { get; set; } = "Source kind";
    [Parameter] public string TargetKindAriaLabel { get; set; } = "Target kind";

    [Parameter] public ConverterShellMode Mode { get; set; } = ConverterShellMode.SingleFile;
    [Parameter] public EventCallback<ConverterShellMode> ModeChanged { get; set; }
    [Parameter] public bool AutoMode { get; set; } = true;
    [Parameter] public bool ShowModeToggle { get; set; } = true;

    [Parameter] public string SourceText { get; set; } = "";
    [Parameter] public EventCallback<string> SourceTextChanged { get; set; }
    [Parameter] public string? SourceFileName { get; set; }
    [Parameter] public EventCallback<string?> SourceFileNameChanged { get; set; }
    [Parameter] public string OutputText { get; set; } = "";
    [Parameter] public EventCallback<string> OutputTextChanged { get; set; }
    [Parameter] public string? OutputFileName { get; set; }
    [Parameter] public EventCallback<string?> OutputFileNameChanged { get; set; }
    [Parameter] public bool OutputReadOnly { get; set; } = true;

    [Parameter] public IReadOnlyList<ConverterFile> SourceFiles { get; set; } = [];
    [Parameter] public EventCallback<IReadOnlyList<ConverterFile>> SourceFilesChanged { get; set; }
    [Parameter] public IReadOnlyList<ConverterFile> OutputFiles { get; set; } = [];
    [Parameter] public EventCallback<IReadOnlyList<ConverterFile>> OutputFilesChanged { get; set; }
    [Parameter] public string? SelectedSourceFileId { get; set; }
    [Parameter] public EventCallback<string?> SelectedSourceFileIdChanged { get; set; }
    [Parameter] public string? SelectedOutputFileId { get; set; }
    [Parameter] public EventCallback<string?> SelectedOutputFileIdChanged { get; set; }

    [Parameter] public string Instructions { get; set; } = "";
    [Parameter] public EventCallback<string> InstructionsChanged { get; set; }
    [Parameter] public bool ShowInstructions { get; set; } = true;
    [Parameter] public string InstructionsLabel { get; set; } = "Additional Instructions";
    [Parameter] public string InstructionsPlaceholder { get; set; } = "Provide any specific requirements to tailor the code conversion.";

    /// <summary>Host convert operation. The shell awaits it and applies a successful result.</summary>
    [Parameter] public IAsyncCallback<ConverterRequest, ConverterResult>? Convert { get; set; }
    [Parameter] public EventCallback OnCleared { get; set; }
    [Parameter] public EventCallback<string> Copied { get; set; }
    [Parameter] public EventCallback<ConverterDownload> Downloaded { get; set; }

    [Parameter] public string SourcePlaceholder { get; set; } = "Your input code here";
    [Parameter] public string OutputPlaceholder { get; set; } = "The translated code will appear here";
    [Parameter] public string ConvertText { get; set; } = "Convert";
    [Parameter] public string ClearText { get; set; } = "Clear";
    [Parameter] public string BusyText { get; set; } = "Converting…";
    [Parameter] public string ShellLabel { get; set; } = "Code converter";
    [Parameter] public string? FooterText { get; set; }
    [Parameter] public string EditorHeight { get; set; } = "320px";

    [Parameter] public long MaxUploadBytes { get; set; } = 5 * 1024 * 1024;
    [Parameter] public int MaxFileCount { get; set; } = 100;
    [Parameter] public IReadOnlyCollection<string> AllowedExtensions { get; set; } = [];

    private readonly CancellationTokenSource _lifetime = new();
    private IJSObjectReference? _module;
    private bool _disposed;
    private bool _modePinned;
    private ConverterShellMode _pinnedMode = ConverterShellMode.SingleFile;
    private bool _parentModeSeen;
    private ConverterShellMode _parentMode = ConverterShellMode.SingleFile;
    private bool _sawAutoMode;
    private bool _previousAutoMode = true;

    public bool IsBusy { get; private set; }
    public bool LastSucceeded { get; private set; } = true;
    public string? StatusMessage { get; private set; }
    public string? LastCopiedText { get; private set; }
    public ConverterDownload? LastDownload { get; private set; }
    public ConverterPane ExpandedPane { get; private set; }
    public bool IsModePinned => _modePinned;

    public bool CanConvert => Enabled && !IsBusy && Convert is not null;
    private bool CanEdit => Enabled && !IsBusy;

    public ConverterShellMode EffectiveMode
    {
        get
        {
            if (!AutoMode) return Mode;
            if (_modePinned) return _pinnedMode;
            // A bound source list is the file browser, including a single file.
            // One converted file does not leave single-file mode; several do.
            if ((SourceFiles?.Count ?? 0) >= 1 || (OutputFiles?.Count ?? 0) > 1)
                return ConverterShellMode.FileBrowser;
            return ConverterShellMode.SingleFile;
        }
    }

    private IReadOnlyList<ConverterKind> SourceKindList => SourceKinds ?? [];
    private IReadOnlyList<ConverterKind> TargetKindList => TargetKinds ?? [];
    private IReadOnlyList<ConverterFile> SourceFileList => SourceFiles ?? [];
    private IReadOnlyList<ConverterFile> OutputFileList => OutputFiles ?? [];
    private string? AcceptList => AllowedExtensions.Count == 0 ? null : string.Join(',', AllowedExtensions.Select(NormalizeExtension));
    private string PanesClass => ExpandedPane == ConverterPane.None ? "fx-converter-panes" : "fx-converter-panes is-expanded";
    private string SourceEditorText => EffectiveMode == ConverterShellMode.FileBrowser ? SelectedSource()?.Content ?? "" : SourceText ?? "";
    private string OutputEditorText => EffectiveMode == ConverterShellMode.FileBrowser ? SelectedOutput()?.Content ?? "" : OutputText ?? "";
    private string? SourceCaption => EffectiveMode == ConverterShellMode.FileBrowser ? SelectedSource()?.DisplayName : SourceFileName;
    private string? OutputCaption => EffectiveMode == ConverterShellMode.FileBrowser ? SelectedOutput()?.DisplayName : OutputFileName;
    private string SourceExpandText => ExpandedPane == ConverterPane.Source ? "Restore source" : "Expand source";
    private string OutputExpandText => ExpandedPane == ConverterPane.Output ? "Restore output" : "Expand output";
    private ConverterDownload CurrentDownload => CreateDownload();

    protected override string? BaseCssClass => "fx-converter";

    protected override void OnParametersSet()
    {
        if (MaxUploadBytes < 1 || MaxUploadBytes > 50L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxUploadBytes), "Use an upload limit between 1 byte and 50 MiB.");
        if (MaxFileCount < 1 || MaxFileCount > 1000)
            throw new ArgumentOutOfRangeException(nameof(MaxFileCount), "Use a file count from 1 to 1000.");

        if (!_parentModeSeen)
        {
            _parentModeSeen = true;
            _parentMode = Mode;
            if (Mode != ConverterShellMode.SingleFile)
            {
                _modePinned = true;
                _pinnedMode = Mode;
            }
        }
        else if (Mode != _parentMode)
        {
            _parentMode = Mode;
            _modePinned = true;
            _pinnedMode = Mode;
        }

        if (_sawAutoMode && !_previousAutoMode && AutoMode)
            _modePinned = false;
        _sawAutoMode = true;
        _previousAutoMode = AutoMode;
    }

    public async Task SetModeAsync(ConverterShellMode mode)
    {
        _modePinned = true;
        _pinnedMode = mode;
        Mode = mode;
        await ModeChanged.InvokeAsync(mode);
        await InvokeAsync(StateHasChanged);
    }

    public async Task SetSourceTextAsync(string? text)
    {
        if (!CanEdit) return;
        var value = text ?? "";
        if (EffectiveMode == ConverterShellMode.FileBrowser)
        {
            var selected = SelectedSource();
            if (selected is null || selected.Content == value) return;
            SourceFiles = ReplaceContent(SourceFileList, selected.Id, value);
            SourceText = value;
            await SourceFilesChanged.InvokeAsync(SourceFiles);
            await SourceTextChanged.InvokeAsync(value);
            await InvokeAsync(StateHasChanged);
            return;
        }
        if (string.Equals(SourceText, value, StringComparison.Ordinal)) return;
        SourceText = value;
        await SourceTextChanged.InvokeAsync(value);
        await InvokeAsync(StateHasChanged);
    }

    public async Task SetOutputTextAsync(string? text)
    {
        if (!CanEdit || OutputReadOnly) return;
        var value = text ?? "";
        if (EffectiveMode == ConverterShellMode.FileBrowser)
        {
            var selected = SelectedOutput();
            if (selected is null || selected.Content == value) return;
            OutputFiles = ReplaceContent(OutputFileList, selected.Id, value);
            OutputText = value;
            await OutputFilesChanged.InvokeAsync(OutputFiles);
            await OutputTextChanged.InvokeAsync(value);
            return;
        }
        if (string.Equals(OutputText, value, StringComparison.Ordinal)) return;
        OutputText = value;
        await OutputTextChanged.InvokeAsync(value);
    }

    public async Task SetInstructionsAsync(string? text)
    {
        if (!CanEdit) return;
        var value = text ?? "";
        if (string.Equals(Instructions, value, StringComparison.Ordinal)) return;
        Instructions = value;
        await InstructionsChanged.InvokeAsync(value);
        await InvokeAsync(StateHasChanged);
    }

    public async Task SelectSourceFileAsync(string? id)
    {
        if (!CanEdit || string.IsNullOrEmpty(id) || SourceFileList.All(file => file.Id != id) || SelectedSourceFileId == id)
            return;
        SelectedSourceFileId = id;
        var selected = SelectedSource();
        SourceText = selected?.Content ?? "";
        SourceFileName = selected?.Name;
        await SelectedSourceFileIdChanged.InvokeAsync(id);
        await SourceTextChanged.InvokeAsync(SourceText);
        await SourceFileNameChanged.InvokeAsync(SourceFileName);
        await InvokeAsync(StateHasChanged);
    }

    public async Task SelectOutputFileAsync(string? id)
    {
        if (!CanEdit || string.IsNullOrEmpty(id) || OutputFileList.All(file => file.Id != id) || SelectedOutputFileId == id)
            return;
        SelectedOutputFileId = id;
        var selected = SelectedOutput();
        OutputText = selected?.Content ?? "";
        OutputFileName = selected?.Name;
        await SelectedOutputFileIdChanged.InvokeAsync(id);
        await OutputTextChanged.InvokeAsync(OutputText);
        await OutputFileNameChanged.InvokeAsync(OutputFileName);
        await InvokeAsync(StateHasChanged);
    }

    public Task ToggleSourceExpandAsync() => ToggleExpandAsync(ConverterPane.Source);
    public Task ToggleOutputExpandAsync() => ToggleExpandAsync(ConverterPane.Output);

    public Task ToggleExpandAsync(ConverterPane pane)
    {
        if (!Enabled || pane == ConverterPane.None) return Task.CompletedTask;
        ExpandedPane = ExpandedPane == pane ? ConverterPane.None : pane;
        return InvokeAsync(StateHasChanged);
    }

    public async Task ImportBrowserFilesAsync(IReadOnlyList<IBrowserFile> files)
    {
        if (!CanEdit) return;
        if (files is null || files.Count == 0)
        {
            Fail("Choose a file to upload.");
            await InvokeAsync(StateHasChanged);
            return;
        }
        if (files.Count > MaxFileCount)
        {
            Fail($"Choose at most {MaxFileCount} files.");
            await InvokeAsync(StateHasChanged);
            return;
        }

        var loaded = new List<ConverterFile>(files.Count);
        foreach (var file in files)
        {
            var text = await ReadUploadAsync(file);
            if (text is null)
            {
                await InvokeAsync(StateHasChanged);
                return;
            }
            var relative = (file.Name ?? "").Replace('\\', '/').Trim().TrimStart('/');
            var name = Path.GetFileName(relative);
            loaded.Add(new ConverterFile
            {
                Name = name,
                RelativePath = string.IsNullOrWhiteSpace(relative) ? name : relative,
                Content = text,
                MediaType = file.ContentType
            });
        }

        if (loaded.Count == 1 && EffectiveMode == ConverterShellMode.SingleFile)
            await ApplySingleAsync(loaded[0]);
        else
            await ApplyManyAsync(loaded);
    }

    public async Task ConvertAsync()
    {
        if (!CanConvert || Convert is null) return;
        IsBusy = true;
        StatusMessage = null;
        LastSucceeded = true;
        await InvokeAsync(StateHasChanged);
        try
        {
            var result = await Convert.InvokeAsync(BuildRequest(), _lifetime.Token);
            if (_disposed) return;
            if (result is null)
            {
                Fail("The convert callback returned no result.");
                return;
            }
            if (!result.Succeeded)
            {
                Fail(string.IsNullOrWhiteSpace(result.StatusMessage) ? "Conversion failed." : result.StatusMessage);
                return;
            }
            await ApplyResultAsync(result);
        }
        catch (OperationCanceledException)
        {
            if (!_disposed) Fail("Conversion was canceled.");
        }
        catch (Exception ex)
        {
            if (!_disposed) Fail(string.IsNullOrWhiteSpace(ex.Message) ? "Conversion failed." : ex.Message);
        }
        finally
        {
            IsBusy = false;
            if (!_disposed) await InvokeAsync(StateHasChanged);
        }
    }

    public async Task ClearAsync()
    {
        if (!CanEdit) return;
        SourceText = "";
        OutputText = "";
        SourceFileName = null;
        OutputFileName = null;
        Instructions = "";
        SourceFiles = [];
        OutputFiles = [];
        SelectedSourceFileId = null;
        SelectedOutputFileId = null;
        StatusMessage = null;
        LastSucceeded = true;
        ExpandedPane = ConverterPane.None;
        await SourceTextChanged.InvokeAsync(SourceText);
        await OutputTextChanged.InvokeAsync(OutputText);
        await SourceFileNameChanged.InvokeAsync(SourceFileName);
        await OutputFileNameChanged.InvokeAsync(OutputFileName);
        await InstructionsChanged.InvokeAsync(Instructions);
        await SourceFilesChanged.InvokeAsync(SourceFiles);
        await OutputFilesChanged.InvokeAsync(OutputFiles);
        await SelectedSourceFileIdChanged.InvokeAsync(null);
        await SelectedOutputFileIdChanged.InvokeAsync(null);
        if (OnCleared.HasDelegate) await OnCleared.InvokeAsync();
        await InvokeAsync(StateHasChanged);
    }

    public async Task CopyOutputAsync()
    {
        if (!CanEdit) return;
        var text = OutputEditorText;
        LastCopiedText = text;
        if (Copied.HasDelegate) await Copied.InvokeAsync(text);
        try
        {
            _module ??= await JS.InvokeAsync<IJSObjectReference>(
                "import",
                $"./_content/{typeof(ConverterShell).Assembly.GetName().Name}/converter-shell.js");
            if (_module is not null)
                await _module.InvokeVoidAsync("copyText", text);
        }
        catch
        {
            // LastCopiedText and Copied already recorded the text.
        }
    }

    public ConverterDownload CreateDownload()
    {
        var file = EffectiveMode == ConverterShellMode.FileBrowser ? SelectedOutput() : null;
        var name = file?.Name ?? OutputFileName;
        if (string.IsNullOrWhiteSpace(name))
            name = string.IsNullOrWhiteSpace(SourceFileName) ? "converted.txt" : SourceFileName;
        return new ConverterDownload
        {
            FileName = SanitizeFileName(name),
            Content = OutputEditorText,
            MediaType = SafeMediaType(file?.MediaType)
        };
    }

    public async Task DownloadOutputAsync()
    {
        if (!CanEdit) return;
        LastDownload = CreateDownload();
        if (Downloaded.HasDelegate) await Downloaded.InvokeAsync(LastDownload);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        _lifetime.Dispose();
        if (_module is not null)
        {
            try { await _module.DisposeAsync(); } catch { /* already gone */ }
        }
    }

    private async Task OnSourceKindChanged(string? id)
    {
        SourceKindId = id;
        await SourceKindIdChanged.InvokeAsync(id);
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnTargetKindChanged(string? id)
    {
        TargetKindId = id;
        await TargetKindIdChanged.InvokeAsync(id);
        await InvokeAsync(StateHasChanged);
    }

    private Task OnModeSelected(string? value)
    {
        if (!Enum.TryParse<ConverterShellMode>(value, out var mode)) return Task.CompletedTask;
        return SetModeAsync(mode);
    }

    private Task OnInstructionsInput(ChangeEventArgs args) => SetInstructionsAsync(args.Value?.ToString());

    private async Task OnUploadAsync(InputFileChangeEventArgs args)
    {
        if (!CanEdit) return;
        var limit = Math.Max(1, MaxFileCount);
        await ImportBrowserFilesAsync(args.GetMultipleFiles(limit).ToList());
    }

    private async Task ApplySingleAsync(ConverterFile file)
    {
        SourceText = file.Content;
        SourceFileName = file.Name;
        StatusMessage = null;
        LastSucceeded = true;
        await SourceTextChanged.InvokeAsync(SourceText);
        await SourceFileNameChanged.InvokeAsync(SourceFileName);
        await InvokeAsync(StateHasChanged);
    }

    private async Task ApplyManyAsync(IReadOnlyList<ConverterFile> incoming)
    {
        var merged = MergeUploads(SourceFileList, incoming);
        SourceFiles = merged;
        var selected = SelectFile(merged, SelectedSourceFileId);
        if (selected is not null)
            SelectedSourceFileId = selected.Id;
        SourceText = selected?.Content ?? "";
        SourceFileName = selected?.Name;
        StatusMessage = null;
        LastSucceeded = true;
        await SourceFilesChanged.InvokeAsync(SourceFiles);
        await SelectedSourceFileIdChanged.InvokeAsync(SelectedSource()?.Id);
        await SourceTextChanged.InvokeAsync(SourceText);
        await SourceFileNameChanged.InvokeAsync(SourceFileName);
        await InvokeAsync(StateHasChanged);
    }

    private async Task ApplyResultAsync(ConverterResult result)
    {
        var files = (result.OutputFiles ?? []).ToList();
        var text = result.OutputText ?? "";
        var name = result.OutputFileName;
        if (files.Count > 0 && text.Length == 0)
            text = files[0].Content ?? "";
        if (string.IsNullOrWhiteSpace(name) && files.Count > 0)
            name = files[0].Name;

        var showBrowser = EffectiveMode == ConverterShellMode.FileBrowser || files.Count > 1;
        if (showBrowser)
        {
            if (files.Count == 0)
            {
                var fileName = SanitizeFileName(string.IsNullOrWhiteSpace(name) ? "converted.txt" : name!);
                files.Add(new ConverterFile { Name = fileName, RelativePath = fileName, Content = text });
            }
            OutputFiles = files;
            SelectedOutputFileId = files[0].Id;
            OutputText = files[0].Content ?? "";
            OutputFileName = files[0].Name;
            await OutputFilesChanged.InvokeAsync(OutputFiles);
            await SelectedOutputFileIdChanged.InvokeAsync(SelectedOutputFileId);
        }
        else
        {
            OutputText = text;
            OutputFileName = name;
            OutputFiles = files;
            await OutputFilesChanged.InvokeAsync(OutputFiles);
        }
        await OutputTextChanged.InvokeAsync(OutputText);
        await OutputFileNameChanged.InvokeAsync(OutputFileName);
        StatusMessage = result.StatusMessage;
        LastSucceeded = true;
    }

    private ConverterRequest BuildRequest()
    {
        var mode = EffectiveMode;
        var selected = mode == ConverterShellMode.FileBrowser ? SelectedSource() : null;
        var text = mode == ConverterShellMode.FileBrowser ? selected?.Content ?? "" : SourceText ?? "";
        var fileName = mode == ConverterShellMode.FileBrowser ? selected?.Name : SourceFileName;
        IReadOnlyList<ConverterFile> files;
        string? selectedId;
        if (mode == ConverterShellMode.FileBrowser)
        {
            files = SourceFileList;
            selectedId = selected?.Id;
        }
        else if (text.Length > 0 || !string.IsNullOrWhiteSpace(fileName))
        {
            var name = string.IsNullOrWhiteSpace(fileName) ? "source.txt" : fileName!;
            files =
            [
                new ConverterFile
                {
                    Id = ConverterFile.SingleFileId,
                    Name = name,
                    RelativePath = name,
                    Content = text
                }
            ];
            selectedId = ConverterFile.SingleFileId;
        }
        else
        {
            files = [];
            selectedId = null;
        }

        return new ConverterRequest
        {
            Mode = mode,
            SourceKindId = SourceKindId,
            TargetKindId = TargetKindId,
            SourceText = text,
            SourceFileName = fileName,
            Instructions = Instructions ?? "",
            SourceFiles = files,
            SelectedSourceFileId = selectedId
        };
    }

    private ConverterFile? SelectedSource() => SelectFile(SourceFileList, SelectedSourceFileId);
    private ConverterFile? SelectedOutput() => SelectFile(OutputFileList, SelectedOutputFileId);

    private static ConverterFile? SelectFile(IReadOnlyList<ConverterFile> files, string? id)
    {
        if (files.Count == 0) return null;
        return files.FirstOrDefault(file => file.Id == id) ?? files[0];
    }

    private static ConverterKind? SelectedKind(IReadOnlyList<ConverterKind>? kinds, string? id)
    {
        if (kinds is null || string.IsNullOrEmpty(id)) return null;
        return kinds.FirstOrDefault(kind => kind.Id == id);
    }

    private async Task<string?> ReadUploadAsync(IBrowserFile file)
    {
        var name = file.Name ?? "";
        var error = UploadValidation.Validate(name, file.Size, MaxUploadBytes, AllowedExtensions);
        if (error is not null)
        {
            Fail(error);
            return null;
        }
        try
        {
            await using var stream = file.OpenReadStream(MaxUploadBytes, _lifetime.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(_lifetime.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(string.IsNullOrWhiteSpace(ex.Message) ? "The file could not be read." : ex.Message);
            return null;
        }
    }

    private void Fail(string message)
    {
        StatusMessage = message;
        LastSucceeded = false;
    }

    private static List<ConverterFile> ReplaceContent(IReadOnlyList<ConverterFile> files, string id, string content)
        => files.Select(file => file.Id == id ? file with { Content = content } : file).ToList();

    private static List<ConverterFile> MergeUploads(IReadOnlyList<ConverterFile> existing, IReadOnlyList<ConverterFile> incoming)
    {
        var merged = existing.ToList();
        foreach (var file in incoming)
        {
            var key = FileKey(file);
            var index = merged.FindIndex(item => string.Equals(FileKey(item), key, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                merged[index] = file with { Id = merged[index].Id };
            else
                merged.Add(file);
        }
        return merged;
    }

    private static string FileKey(ConverterFile file)
    {
        var path = string.IsNullOrWhiteSpace(file.RelativePath) ? file.Name : file.RelativePath;
        return path.Replace('\\', '/').Trim().TrimStart('/');
    }

    private static string SafeMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType)) return "text/plain";
        var type = mediaType.Trim();
        foreach (var ch in type)
        {
            var ok = char.IsAsciiLetterOrDigit(ch) || ch is '/' or '.' or '+' or '-' or ';' or '=';
            if (!ok) return "text/plain";
        }
        return type.Length == 0 ? "text/plain" : type;
    }

    private static string SanitizeFileName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        var slash = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        var file = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        if (string.IsNullOrWhiteSpace(file) || file is "." or "..") return "converted.txt";
        foreach (var invalid in Path.GetInvalidFileNameChars())
            file = file.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(file) ? "converted.txt" : file;
    }

    private static string NormalizeExtension(string extension)
    {
        var trimmed = extension.Trim();
        return trimmed.StartsWith('.') ? trimmed : "." + trimmed;
    }
}
