using System.Text;
using System.Text.RegularExpressions;
using Fx.ControlKit;
using Fx.ControlKit.Conversion;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

internal static class ConverterChecks
{
    public static async Task Run(HtmlRenderer renderer, List<IComponent> components, Action<bool, string> check)
    {
        void Reject(Action action, string name)
        {
            try { action(); }
            catch (ArgumentNullException) { check(true, name); return; }
            throw new Exception(name);
        }
        async Task RejectParameters(ConverterShell shell, (string Name, object? Value)[] values, string name)
        {
            try
            {
                await shell.SetParametersAsync(P(values));
            }
            catch (ArgumentOutOfRangeException)
            {
                check(true, name);
                return;
            }
            throw new Exception(name);
        }

        Reject(() => HostCallback.Create<string, string>((Func<string, Task<string>>)null!), "null convert delegate is rejected");
        check(ConverterEditor.CountLines(null) == 1 && ConverterEditor.CountLines("") == 1, "empty editor is one line");
        check(ConverterEditor.CountLines("a\nb\nc") == 3 && ConverterEditor.CountLines("a\n") == 2, "line count follows newline characters");
        check(new ConverterDownload { FileName = "out.txt", Content = "a+b\nc" }.Href == "data:text/plain;charset=utf-8," + Uri.EscapeDataString("a+b\nc"), "download href is a UTF-8 data URL");
        check(ConverterResult.Failed("nope").Succeeded == false && ConverterResult.FromFiles([new ConverterFile { Name = "a.js", Content = "1" }]).OutputText == "1", "convert results expose text and failure");

        var kinds = new List<ConverterKind>
        {
            new() { Id = "cr", Label = "Crystal", Description = "Report" },
            new() { Id = "ex", Label = "Exstream" }
        };
        var shellRoot = await renderer.RenderComponentAsync<ConverterShell>(P(
            ("SourceKinds", kinds),
            ("TargetKinds", kinds),
            ("SourceKindId", "cr"),
            ("TargetKindId", "ex"),
            ("FooterText", "2 of 2 free uses left today")));
        var shell = components.OfType<ConverterShell>().Last();
        var html = shellRoot.ToHtmlString();
        check(html.Contains("aria-label=\"Source kind\"") && html.Contains("aria-label=\"Target kind\""), "kind dropdowns expose source and target labels");
        check(html.Contains("Crystal") && html.Contains("Exstream"), "kind dropdowns render the host list");
        check(!html.Contains("Python") && !html.Contains("JavaScript") && !html.Contains("Mutarjim"), "kind dropdowns do not embed a language catalog");
        check(html.Contains("Your input code here") && html.Contains("The translated code will appear here"), "editors use the converter placeholders");
        check(html.Contains("data-line-count=\"1\"") && html.Contains("aria-label=\"Source\"") && html.Contains("aria-label=\"Converted output\""), "both editors render a line gutter");
        check(html.Contains("Have a file?") && html.Contains("Upload") && html.Contains(">Copy<") && html.Contains(">Download<"), "upload, copy, and download are in the panes");
        check(html.Contains("Expand source") && html.Contains("Expand output"), "each pane can expand");
        check(html.Contains(">Convert<") && html.Contains(">Clear<") && html.Contains("Additional Instructions"), "convert, clear, and instructions are present");
        check(html.Contains("Single file") && html.Contains("File browser") && html.Contains("data-mode=\"SingleFile\""), "mode toggle starts on the single-file layout");
        check(!html.Contains("Run / Preview"), "run preview stays off until the host enables it");
        check(html.Contains("2 of 2 free uses left today"), "footer text is host content");
        check(html.Contains("btn-success") && ConvertButton(html).Contains("disabled"), "convert stays disabled until a callback is bound");
        check(!ClearButton(html).Contains("disabled"), "clear stays available without a convert callback");

        var sourceKind = "";
        shell.SourceKindIdChanged = EventCallback.Factory.Create<string?>(new object(), value => sourceKind = value ?? "");
        var sourceDrop = components.OfType<DropDownListControl<string, ConverterKind>>().Last(drop => drop.AriaLabel == "Source kind");
        await sourceDrop.ValueChanged.InvokeAsync("ex");
        check(shell.SourceKindId == "ex" && sourceKind == "ex", "source kind dropdown updates the bound id");

        await shell.SetSourceTextAsync("alpha\nbeta\ngamma");
        await shell.SetInstructionsAsync("keep names");
        check(shellRoot.ToHtmlString().Contains("data-line-count=\"3\"") && shellRoot.ToHtmlString().Contains("keep names"), "source edits refresh line numbers and instructions");

        ConverterRequest? seen = null;
        var tokenCanCancel = false;
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        shell.Convert = HostCallback.Create<ConverterRequest, ConverterResult>(async (request, token) =>
        {
            seen = request;
            tokenCanCancel = token.CanBeCanceled;
            entered.TrySetResult();
            await release.Task;
            return ConverterResult.FromText("out-1\nout-2", "Program.js", "Translated");
        });
        var pending = shell.ConvertAsync();
        await entered.Task;
        check(shell.IsBusy && shellRoot.ToHtmlString().Contains("Converting"), "convert waits on the host callback");
        check(tokenCanCancel, "convert callback receives a cancellation token");
        check(seen is { SourceKindId: "ex", TargetKindId: "ex", SourceText: "alpha\nbeta\ngamma", Instructions: "keep names", Mode: ConverterShellMode.SingleFile }
            && seen.SourceFiles.Count == 1
            && seen.SourceFiles[0].Id == ConverterFile.SingleFileId
            && seen.SourceFiles[0].Content == "alpha\nbeta\ngamma", "single-file request carries kinds, text, instructions, and one synthetic file");
        release.SetResult();
        await pending;
        check(!shell.IsBusy && shell.OutputText == "out-1\nout-2" && shell.OutputFileName == "Program.js" && shell.LastSucceeded, "successful convert fills the output editor");
        check(shellRoot.ToHtmlString().Contains("out-1") && shellRoot.ToHtmlString().Contains("role=\"status\"") && shellRoot.ToHtmlString().Contains("Translated"), "success status is visible");
        check(shellRoot.ToHtmlString().Contains("download=\"Program.js\""), "download uses the converted file name");

        var outputChanges = 0;
        shell.OutputTextChanged = EventCallback.Factory.Create<string>(new object(), _ => outputChanges++);
        shell.Convert = HostCallback.Create<ConverterRequest, ConverterResult>(_ => Task.FromResult(ConverterResult.Failed("Could not translate")));
        await shell.ConvertAsync();
        check(shell.OutputText == "out-1\nout-2" && outputChanges == 0 && shellRoot.ToHtmlString().Contains("role=\"alert\"") && shellRoot.ToHtmlString().Contains("Could not translate"), "a failed convert keeps the previous output");

        shell.Convert = HostCallback.Create<ConverterRequest, ConverterResult>(_ => throw new InvalidOperationException("boom <script>"));
        await shell.ConvertAsync();
        var failedHtml = shellRoot.ToHtmlString();
        check(failedHtml.Contains("boom") && failedHtml.Contains("&lt;script&gt;") && !failedHtml.Contains("<script>"), "convert exceptions are encoded status text");
        check(shell.OutputText == "out-1\nout-2", "exceptions leave the output in place");

        shell.Enabled = false;
        var disabledCalls = 0;
        shell.Convert = HostCallback.Create<ConverterRequest, ConverterResult>(_ => { disabledCalls++; return Task.FromResult(ConverterResult.FromText("no")); });
        await shell.ConvertAsync();
        check(disabledCalls == 0, "disabled shell does not call convert");
        shell.Enabled = true;

        var copied = "";
        shell.Copied = EventCallback.Factory.Create<string>(new object(), value => copied = value);
        await shell.CopyOutputAsync();
        check(shell.LastCopiedText == "out-1\nout-2" && copied == "out-1\nout-2", "copy records the visible output");
        await shell.DownloadOutputAsync();
        check(shell.LastDownload is { FileName: "Program.js", Content: "out-1\nout-2" }, "download records the visible output");
        check(shell.CreateDownload().Href.Contains(Uri.EscapeDataString("out-1\nout-2")), "download link contains the output text");

        shell.OutputFileName = "..\\..\\evil.txt";
        check(shell.CreateDownload().FileName == "evil.txt", "download file names drop directories");
        shell.OutputFileName = "Program.js";

        var cleared = 0;
        var sourceAfterClear = "stale";
        shell.OnCleared = EventCallback.Factory.Create(new object(), () => cleared++);
        shell.SourceTextChanged = EventCallback.Factory.Create<string>(new object(), value => sourceAfterClear = value);
        var kindDuringClear = shell.SourceKindId;
        await shell.ClearAsync();
        check(cleared == 1 && shell.SourceText == "" && shell.OutputText == "" && shell.Instructions == "" && sourceAfterClear == "", "clear resets both editors and instructions");
        check(shell.SourceKindId == kindDuringClear && shell.TargetKindId == "ex", "clear keeps the selected kinds");
        check(!shellRoot.ToHtmlString().Contains("out-1"), "cleared output leaves the editor");

        await shell.ImportBrowserFilesAsync([new FakeFile("Program.cr", "print(1)\nprint(2)")]);
        check(shell.EffectiveMode == ConverterShellMode.SingleFile && shell.SourceText == "print(1)\nprint(2)" && shell.SourceFileName == "Program.cr", "one upload fills the single-file editor");
        check(shellRoot.ToHtmlString().Contains("data-mode=\"SingleFile\"") && shellRoot.ToHtmlString().Contains("Program.cr"), "single-file upload stays on that layout");

        await shell.SetParametersAsync(P(("MaxUploadBytes", 4L)));
        await shell.ImportBrowserFilesAsync([new FakeFile("big.cr", "hello")]);
        check(shell.SourceText == "print(1)\nprint(2)" && shell.StatusMessage is not null && shellRoot.ToHtmlString().Contains("role=\"alert\""), "oversized uploads are rejected");
        await shell.SetParametersAsync(P(("MaxUploadBytes", 1024L), ("AllowedExtensions", new[] { ".cr" })));
        await shell.ImportBrowserFilesAsync([new FakeFile("notes.exe", "x")]);
        check(shell.SourceText == "print(1)\nprint(2)" && (shell.StatusMessage?.Contains("extension", StringComparison.OrdinalIgnoreCase) ?? false), "disallowed extensions are rejected");
        await shell.SetParametersAsync(P(("AllowedExtensions", Array.Empty<string>())));

        await shell.ImportBrowserFilesAsync([
            new FakeFile("src/one.cr", "one-body"),
            new FakeFile("src/two.cr", "two-body")
        ]);
        check(shell.EffectiveMode == ConverterShellMode.FileBrowser && shell.IsModePinned == false, "two uploads switch to the file browser automatically");
        var browserHtml = shellRoot.ToHtmlString();
        check(browserHtml.Contains("data-mode=\"FileBrowser\"") && browserHtml.Contains("aria-label=\"Source files\"") && browserHtml.Contains("one.cr") && browserHtml.Contains("two.cr"), "file browser lists source files");
        check(browserHtml.Contains("one-body") && !browserHtml.Contains("two-body"), "file browser shows the selected source");
        var second = shell.SourceFiles.Single(file => file.Name == "two.cr");
        await shell.SelectSourceFileAsync(second.Id);
        check(shell.SourceText == "two-body" && shellRoot.ToHtmlString().Contains("two-body") && !shellRoot.ToHtmlString().Contains("one-body"), "selecting a source file shows its content");

        var filesBeforeEdit = shell.SourceFiles.ToList();
        await shell.SetSourceTextAsync("two-edited");
        check(shell.SourceFiles.Single(file => file.Id == second.Id).Content == "two-edited"
            && shell.SourceFiles.Single(file => file.Name == "one.cr").Content == "one-body"
            && filesBeforeEdit.Single(file => file.Name == "one.cr").Content == "one-body", "editing the open source replaces only that file");

        var firstId = shell.SourceFiles.Single(file => file.Name == "one.cr").Id;
        await shell.ImportBrowserFilesAsync([new FakeFile("SRC/ONE.CR", "one-replaced")]);
        check(shell.SourceFiles.Count == 2
            && shell.SourceFiles.Single(file => file.Id == firstId).Content == "one-replaced", "a repeated path replaces that source and keeps its id");

        IReadOnlyList<ConverterFile>? converted = null;
        ConverterRequest? browserRequest = null;
        shell.Convert = new HostConvert((request, _) =>
        {
            browserRequest = request;
            return Task.FromResult(ConverterResult.FromFiles([
                new ConverterFile { Name = "one.ex", RelativePath = "out/one.ex", Content = "ex-one" },
                new ConverterFile { Name = "two.ex", RelativePath = "out/two.ex", Content = "ex-two" }
            ], "Two files"));
        });
        shell.OutputFilesChanged = EventCallback.Factory.Create<IReadOnlyList<ConverterFile>>(new object(), files => converted = files);
        await shell.ConvertAsync();
        check(browserRequest is { Mode: ConverterShellMode.FileBrowser, SourceText: "two-edited" }
            && browserRequest.SourceFiles.Count == 2
            && browserRequest.SourceFiles.Any(file => file.Content == "one-replaced"), "file-browser convert receives the open source and every file");
        check(shell.OutputFiles.Count == 2 && converted?.Count == 2 && shellRoot.ToHtmlString().Contains("aria-label=\"Converted files\""), "converted files show in the output browser");
        check(shellRoot.ToHtmlString().Contains("ex-one") && shellRoot.ToHtmlString().Contains("one.ex"), "the first converted file is selected");
        await shell.SelectOutputFileAsync(shell.OutputFiles[1].Id);
        check(shell.OutputText == "ex-two" && shellRoot.ToHtmlString().Contains("ex-two") && !shellRoot.ToHtmlString().Contains("ex-one"), "selecting a converted file shows its content");
        await shell.CopyOutputAsync();
        check(shell.LastCopiedText == "ex-two", "copy uses the selected converted file");

        await shell.SetModeAsync(ConverterShellMode.SingleFile);
        check(shell.IsModePinned && shell.EffectiveMode == ConverterShellMode.SingleFile && shell.SourceFiles.Count == 2, "the mode toggle pins single-file without dropping the file list");
        check(shellRoot.ToHtmlString().Contains("data-mode=\"SingleFile\"") && !shellRoot.ToHtmlString().Contains("aria-label=\"Source files\""), "pinned single-file hides the file lists");
        ConverterRequest? pinnedRequest = null;
        shell.Convert = HostCallback.Create<ConverterRequest, ConverterResult>(request =>
        {
            pinnedRequest = request;
            return Task.FromResult(ConverterResult.FromText("pinned-out"));
        });
        await shell.ConvertAsync();
        check(pinnedRequest is { Mode: ConverterShellMode.SingleFile }
            && pinnedRequest.SourceFiles.Count == 1
            && pinnedRequest.SourceFiles[0].Id == ConverterFile.SingleFileId, "pinned single-file convert sends the open editor");
        check(shell.OutputText == "pinned-out", "pinned single-file convert writes the output editor");
        shell.OutputReadOnly = false;
        await shell.SetOutputTextAsync("host-edit");
        check(shell.OutputText == "host-edit", "output can be edited when the host allows it");
        shell.OutputReadOnly = true;
        await shell.SetOutputTextAsync("blocked");
        check(shell.OutputText == "host-edit", "read-only output ignores edits");

        await shell.SetParametersAsync(P(("AutoMode", false)));
        await shell.SetParametersAsync(P(("AutoMode", true)));
        check(!shell.IsModePinned && shell.EffectiveMode == ConverterShellMode.FileBrowser, "turning automatic mode back on follows the source list");

        await shell.ToggleSourceExpandAsync();
        check(shell.ExpandedPane == ConverterPane.Source && shellRoot.ToHtmlString().Contains("Restore source") && !shellRoot.ToHtmlString().Contains("aria-label=\"Converted output\""), "expand source hides the output pane");
        await shell.ToggleSourceExpandAsync();
        check(shell.ExpandedPane == ConverterPane.None && shellRoot.ToHtmlString().Contains("aria-label=\"Converted output\""), "expand source again restores both panes");

        await shell.SetParametersAsync(P(("ShowInstructions", false), ("FooterText", null)));
        check(!shellRoot.ToHtmlString().Contains("Additional Instructions") && !shellRoot.ToHtmlString().Contains("free uses"), "instructions and footer can be omitted");

        var forced = await renderer.RenderComponentAsync<ConverterShell>(P(
            ("Mode", ConverterShellMode.FileBrowser),
            ("AutoMode", true),
            ("ShowInstructions", false),
            ("ShowModeToggle", false)));
        var forcedShell = components.OfType<ConverterShell>().Last();
        check(forcedShell.IsModePinned && forcedShell.EffectiveMode == ConverterShellMode.FileBrowser, "an initial file-browser mode is kept");
        check(forced.ToHtmlString().Contains("No source files") && forced.ToHtmlString().Contains("No converted files") && !forced.ToHtmlString().Contains("Single file"), "empty file browser has a list placeholder and can hide the toggle");

        var owned = await renderer.RenderComponentAsync<ConverterShell>(P(
            ("AutoMode", false),
            ("Mode", ConverterShellMode.SingleFile),
            ("SourceText", "owned"),
            ("SourceKinds", new List<ConverterKind> { new() { Id = "a", Label = "Alpha" } }),
            ("SourceKindId", "a")));
        check(owned.ToHtmlString().Contains("Alpha") && !owned.ToHtmlString().Contains("Crystal"), "replacing the kind list replaces the dropdown text");

        await RejectParameters(shell, [("MaxUploadBytes", 0L)], "a zero upload limit is rejected");
        await RejectParameters(shell, [("MaxFileCount", 0)], "a zero file count is rejected");
        await shell.SetParametersAsync(P(("MaxUploadBytes", 50L * 1024 * 1024), ("MaxFileCount", 1000)));
        check(shell.MaxUploadBytes == 50L * 1024 * 1024 && shell.MaxFileCount == 1000, "upload limits accept the documented upper bounds");
    }

    private static string ConvertButton(string html) => MatchButton(html, "fx-converter-convert");
    private static string ClearButton(string html) => MatchButton(html, "fx-converter-clear");
    private static string MatchButton(string html, string cssClass) =>
        Regex.Match(html, $"<button\\b[^>]*{cssClass}\\b[^>]*>", RegexOptions.IgnoreCase).Value;

    private static ParameterView P(params (string Name, object? Value)[] values) =>
        ParameterView.FromDictionary(values.ToDictionary(v => v.Name, v => v.Value)!);

    private sealed class HostConvert(Func<ConverterRequest, CancellationToken, Task<ConverterResult>> callback) : IAsyncCallback<ConverterRequest, ConverterResult>
    {
        public Task<ConverterResult> InvokeAsync(ConverterRequest argument, CancellationToken cancellationToken) => callback(argument, cancellationToken);
    }

    private sealed class FakeFile(string name, string content, string contentType = "text/plain") : IBrowserFile
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(content);
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public long Size => _bytes.Length;
        public string ContentType => contentType;
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            if (_bytes.Length > maxAllowedSize) throw new IOException("The file exceeds the read limit.");
            return new MemoryStream(_bytes, writable: false);
        }
    }
}
