using System.Text.Json;
using Fx.ControlKit.Preview;
using Fx.ControlKit.WorkTracking;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

internal static class PreviewFacilityChecks
{
    public static async Task Run(HtmlRenderer renderer, Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "fx-preview-facility-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new LocalWorkBoardStore(new(root));
            check(await store.GetPreferenceAsync(LocalWorkBoardStore.LastOpenedProjectPreference) is null, "a new board has no last-opened project");
            await store.SetPreferenceAsync(LocalWorkBoardStore.LastOpenedProjectPreference, "VB6QUOTE");
            check(await store.GetPreferenceAsync(LocalWorkBoardStore.LastOpenedProjectPreference) == "VB6QUOTE", "the last-opened project is remembered");
            await store.SetPreferenceAsync(LocalWorkBoardStore.LastOpenedProjectPreference, "  ");
            check(await store.GetPreferenceAsync(LocalWorkBoardStore.LastOpenedProjectPreference) is null, "a blank preference is cleared");

            var sprint = JsonSerializer.Deserialize<WorkSprint>("""{"Key":"S1","Name":"One","Goal":"Ship","Order":1,"DurationDays":10}""", LocalWorkBoardStore.Json)!;
            check(sprint.CapacityPoints == 0 && sprint.StartsOn is null && sprint.Schedule == "10 days", "an older sprint loads without dates or capacity");
            var dated = new WorkSprint("S1", "Quote", "Port it", 1, 10, new DateOnly(2026, 4, 6), 16);
            check(dated.Schedule == "2026-04-06 – 2026-04-15", "a sprint schedule is inclusive");

            var over = Project("OVER", 1, 3);
            try
            {
                await store.CreateAsync(over.Project, over.Items);
                check(false, "a sprint over its story-point capacity is rejected");
            }
            catch (ArgumentException ex)
            {
                check(ex.Message.Contains("over capacity", StringComparison.Ordinal), "a sprint over its story-point capacity is rejected");
            }
            var fit = Project("FITS", 4, 3);
            var saved = await store.CreateAsync(fit.Project, fit.Items);
            check(saved.Project.Sprints[0].CapacityPoints == 4 && saved.Items.Where(i => i.Kind == WorkItemKind.Story).Sum(i => i.EstimatePoints) == 3, "a sprint within capacity is stored");

            var form = Vb6FormParser.Parse("""
                VERSION 5.00
                Begin VB.Form FQuote
                   Caption         =   "Migration VB6 Test 1 - Quote Calculator"
                   ClientHeight    =   5400
                   ClientWidth     =   7800
                   Begin VB.TextBox txtQuantity
                      Text            =   "2"
                      Left            =   3000
                      Top             =   900
                      Width           =   2400
                      Height          =   375
                   End
                   Begin VB.CheckBox chkTax
                      Caption         =   "Apply sales tax (13%)"
                      Value           =   1
                   End
                   Begin VB.CommandButton cmdCalculate
                      Caption         =   "Calculate"
                   End
                End
                Attribute VB_Name = "FQuote"
                """);
            check(form.Name == "FQuote" && form.Caption.Contains("Quote Calculator", StringComparison.Ordinal)
                && form.ClientWidthPixels == 520 && form.Controls.Count == 3
                && form.Controls.Single(c => c.Name == "txtQuantity").Text == "2"
                && form.Controls.Single(c => c.Name == "chkTax").Value == 1, "a VB6 form designer block becomes a layout");

            var javaDir = Path.Combine(root, "java");
            Directory.CreateDirectory(javaDir);
            var java = LanguageLaunch.JavaSources(javaDir, "quote.QuoteFrame", ["src/main/java/quote/QuoteFrame.java"]);
            check(java.Kind == ProgramPreviewKind.Gui && java.Arguments.Any(arg => arg.Contains("javac", StringComparison.Ordinal) && arg.Contains("quote.QuoteFrame", StringComparison.Ordinal)), "a Java launch compiles sources and runs the main class");
            var python = LanguageLaunch.PythonScript(javaDir, "quote_window.py", "PySide6");
            check(python.Arguments.Any(arg => arg.Contains("PySide6", StringComparison.Ordinal) && arg.Contains("quote_window.py", StringComparison.Ordinal)), "a Python launch checks the toolkit then runs the script");
            check(python.Environment != null && python.Environment.TryGetValue("PYTHONPATH", out var site) && Directory.Exists(site), "a Python launch keeps the user site packages after the preview replaces HOME");
            check(LanguageLaunch.JavaSources(javaDir, "quote.QuoteFrame", ["src/main/java/quote/QuoteFrame.java"], offscreen: true).Offscreen, "a Java desktop launch can opt into offscreen painting");

            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var switcher = await renderer.RenderComponentAsync<WorkProjectSwitcher>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Id"] = "apps",
                    ["Choices"] = WorkProjectSwitcher.FromProjects([new WorkProject { Key = "VB6QUOTE", Name = "VB6 Quote", SourceFingerprint = "x" }]),
                    ["Value"] = "VB6QUOTE"
                }));
                var html = switcher.ToHtmlString();
                check(html.Contains("VB6QUOTE", StringComparison.Ordinal) && html.Contains("VB6 Quote", StringComparison.Ordinal), "the application switcher lists the project key and name");

                var group = await renderer.RenderComponentAsync<ProgramPreviewGroup>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["ShowOrientationToggle"] = true,
                    ["Orientation"] = ProgramPreviewOrientation.Horizontal,
                    ["Items"] = new ProgramPreviewPane[]
                    {
                        new() { Label = "VB6", Content = builder => builder.AddMarkupContent(0, "<p>Quote Calculator</p>") },
                        new() { Label = "Java", Content = builder => builder.AddMarkupContent(0, "<p>Swing</p>") }
                    }
                }));
                var groupHtml = group.ToHtmlString();
                check(groupHtml.Contains("data-orientation=\"horizontal\"", StringComparison.Ordinal)
                    && groupHtml.Contains("Side by side", StringComparison.Ordinal)
                    && groupHtml.Contains(">Stack<", StringComparison.Ordinal)
                    && groupHtml.Contains("Quote Calculator", StringComparison.Ordinal)
                    && groupHtml.Contains("fx-preview-static", StringComparison.Ordinal), "a group can sit side by side and host a static fallback pane");
            });
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static (WorkProject Project, WorkItem[] Items) Project(string key, int capacity, int points)
    {
        WorkItem Item(int n, WorkItemKind kind, string? parent, int estimate = 0) => new()
        {
            Key = key + "-" + n, StableId = key.ToLowerInvariant() + "-" + n, Title = kind + " " + n, Kind = kind, ParentKey = parent,
            AcceptanceCriteria = ["Done when the quote matches."], SprintKey = kind is WorkItemKind.Story or WorkItemKind.Task ? "S1" : "",
            EstimatePoints = estimate
        };
        return (new WorkProject
        {
            Key = key, Name = key, SourceFingerprint = "snap",
            Sprints = [new WorkSprint("S1", "Only", "Ship the quote", 1, 10, new DateOnly(2026, 4, 6), capacity)]
        },
        [
            Item(1, WorkItemKind.Feature, null),
            Item(2, WorkItemKind.Epic, key + "-1"),
            Item(3, WorkItemKind.Story, key + "-2", points),
            Item(4, WorkItemKind.Task, key + "-3")
        ]);
    }
}
