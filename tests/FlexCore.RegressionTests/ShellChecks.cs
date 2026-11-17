using System.Text.RegularExpressions;
using Fx.ControlKit;
using Fx.ControlKit.Shell;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

internal static class ShellChecks
{
    public static async Task Run(HtmlRenderer renderer, List<IComponent> components, Action<bool, string> check)
    {
        var menu = ShellToolCatalog.Menu;
        check(menu.Select(card => card.Id).Distinct(StringComparer.Ordinal).Count() == menu.Count, "tool catalog ids are unique");
        check(menu.Select(card => card.Route).Distinct(StringComparer.Ordinal).Count() == menu.Count, "tool catalog routes are unique");
        check(menu[0].Title == "Code Generator" && menu[1].Title == "Code Assistant" && menu[^1].Title == "Comment Generator", "tool menu keeps the tools-menu order and adds comments");
        check(menu.Any(card => card.Id == "converter" && card.Color == "#e8590c" && card.Route == "converter"), "converter card carries id, color, and route");
        check(ShellToolCatalog.Find("diagram-to-code")?.Title == "Diagram to Code", "catalog lookup accepts the route id");
        check(ShellToolCatalog.GetStarted.Count == 3 && ShellToolCatalog.GetStarted[0].ActionLabel == "Convert Code Now", "get started cards are the three launch actions");

        var converter = ShellToolCatalog.ConverterWithSteps(
            ShellWizard.Step(1, "upload", "Upload", ShellStepState.Done),
            ShellWizard.Step(2, "inventory", "Inventory", ShellStepState.Done),
            ShellWizard.Step(3, "convert", "Convert", ShellStepState.Active),
            ShellWizard.Step(4, "todo", "TODO", ShellStepState.Disabled),
            ShellWizard.Step(5, "run", "Run", ShellStepState.Pending));
        var sections = ShellToolCatalog.CreateSidebar(converter);
        string? active = "home";
        ShellNavigation? navigation = null;
        string? planAction = null;
        var shellRoot = await renderer.RenderComponentAsync<AppShell>(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Brand"] = "CodeConvert",
            ["WorkspaceLabel"] = "Your Workspace",
            ["WorkspaceName"] = "Personal",
            ["Sections"] = sections,
            ["ActiveItemId"] = active,
            ["ActiveItemIdChanged"] = EventCallback.Factory.Create<string?>(new object(), value => active = value),
            ["OnNavigate"] = EventCallback.Factory.Create<ShellNavigation>(new object(), value => navigation = value),
            ["Plan"] = new ShellPlan { Title = "Pro Plan", Message = "Upgrade to Premium for the best usage limits!", ActionLabel = "Upgrade Plan" },
            ["OnPlanAction"] = EventCallback.Factory.Create<string>(new object(), value => planAction = value),
            ["FooterItems"] = new List<ShellNavItem> { ShellNav.Item("account", "Account", ShellIcon.Account) },
            ["Footer"] = (RenderFragment)(builder => builder.AddMarkupContent(0, "<span class=\"fx-shell-account\">Plans &amp; Billing</span>")),
            ["Banner"] = (RenderFragment)(builder => builder.AddMarkupContent(0, "You have CodeConvert Pro Plan access.")),
            ["ChildContent"] = (RenderFragment)(builder => builder.AddMarkupContent(0, "<p>Home body</p>")),
            ["Theme"] = ShellTheme.Light
        }));
        var shell = components.OfType<AppShell>().Last();
        var html = shellRoot.ToHtmlString();
        check(html.Contains("data-theme=\"light\"") && html.Contains("CodeConvert") && html.Contains("Personal"), "shell shows the brand, workspace, and light theme");
        check(html.Contains("aria-current=\"page\"") && html.Contains("data-item=\"home\""), "home starts current");
        check(html.Contains("data-item=\"explainer\"") && html.Contains("Code Reviewer") && html.Contains("Diagram to Code"), "sidebar lists the catalog tools");
        check(html.Contains("data-step-state=\"done\"") && html.Contains("data-item=\"upload\"") && html.Contains("Upload"), "wizard steps render their labels and done state");
        check(html.Contains("data-step-state=\"done\"") && html.Contains("&#x2713;"), "done steps show a check");
        check(html.Contains("data-item=\"todo\"") && html.Contains("data-step-state=\"disabled\""), "disabled wizard step is marked");
        check(Regex.IsMatch(html, "data-item=\"todo\"[^>]*disabled") || Regex.IsMatch(html, "disabled[^>]*data-item=\"todo\""), "disabled step cannot be clicked");
        check(html.Contains("Pro Plan") && html.Contains("Upgrade Plan") && html.Contains("Plans &amp; Billing") && html.Contains("Account"), "footer keeps the plan card and account");
        check(html.Contains("You have CodeConvert Pro Plan access.") && html.Contains("Home body"), "banner and main content render");
        check(html.Contains("aria-label=\"Show navigation\""), "narrow screens have a menu button");

        await shell.SelectAsync("todo");
        check(active == "home" && shell.ActiveItemId == "home", "a disabled step does not change the active item");
        await shell.SelectAsync("inventory");
        check(active == "inventory" && navigation is { Id: "inventory", ParentId: "converter", StepNumber: 2 }, "a wizard step reports its parent and number");
        check(shellRoot.ToHtmlString().Contains("data-item=\"inventory\"") && shellRoot.ToHtmlString().Contains("aria-current=\"page\""), "the selected step is current");

        await shell.SetCollapsedAsync(true);
        var collapsed = shellRoot.ToHtmlString();
        check(collapsed.Contains("data-collapsed=\"true\"") && !collapsed.Contains("data-item=\"upload\""), "collapsing the sidebar hides wizard steps");
        await shell.SetCollapsedAsync(false);
        await shell.ToggleGroupAsync("converter");
        check(!shellRoot.ToHtmlString().Contains("data-item=\"upload\""), "the converter group can collapse its steps");
        await shell.ToggleGroupAsync("converter");

        await shell.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Theme"] = ShellTheme.Dark }));
        check(shellRoot.ToHtmlString().Contains("data-theme=\"dark\""), "the shell switches to the dark theme");

        string? card = null;
        string? tile = null;
        var homeRoot = await renderer.RenderComponentAsync<ShellHome>(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Title"] = "Home",
            ["Subtitle"] = "Access powerful AI tools to convert, explain, and generate code in one place.",
            ["Cards"] = ShellToolCatalog.GetStarted,
            ["Pinned"] = new List<ShellToolTile>
            {
                new() { Id = "ruby-explainer", Title = "Ruby Code Explainer", LanguageId = "ruby" },
                new() { Id = "python-c", Title = "Python to C", SourceLanguageId = "python", TargetLanguageId = "c" },
                new() { Id = "clojure-gen", Title = "Clojure Code Generator", LanguageId = "clojure" }
            },
            ["PinnedSubtitle"] = "Your pinned tools for quick access.",
            ["Popular"] = new List<ShellToolTile> { new() { Id = "popular-py", Title = "Python Explainer", LanguageId = "python", ShowPin = false } },
            ["OnCardAction"] = EventCallback.Factory.Create<string>(new object(), value => card = value),
            ["OnTileSelected"] = EventCallback.Factory.Create<string>(new object(), value => tile = value)
        }));
        var home = components.OfType<ShellHome>().Last();
        var homeHtml = homeRoot.ToHtmlString();
        check(homeHtml.Contains("Get started") && homeHtml.Contains("Convert Code Now") && homeHtml.Contains("Get Code Explanation"), "get started cards render titles and actions");
        check(homeHtml.Contains("Python to C") && homeHtml.Contains("fx-tile-arrow") && homeHtml.Contains("Ruby Code Explainer"), "pinned tiles show a source-to-target arrow");
        check(homeHtml.Contains(">Py<") && homeHtml.Contains(">C<"), "language marks use the catalog abbreviations");
        await home.ActivateCardAsync("explainer");
        await home.ActivateTileAsync("python-c");
        check(card == "explainer" && tile == "python-c", "cards and tiles raise their ids");

        string? selected = null;
        var gridRoot = await renderer.RenderComponentAsync<ShellToolGrid>(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Title"] = "Tools",
            ["Tools"] = ShellToolCatalog.Menu,
            ["ActiveId"] = "converter",
            ["Theme"] = ShellTheme.Dark,
            ["OnSelect"] = EventCallback.Factory.Create<string>(new object(), value => selected = value)
        }));
        var grid = components.OfType<ShellToolGrid>().Last();
        var gridHtml = gridRoot.ToHtmlString();
        check(gridHtml.Contains("data-theme=\"dark\"") && gridHtml.Contains("Generate code snippets easily and efficiently."), "the tool grid renders catalog descriptions");
        check(gridHtml.Contains("background:#e8590c") && gridHtml.Contains("data-route=\"runner\""), "each card exposes its color and route");
        check(gridHtml.Contains("data-tool=\"converter\"") && gridHtml.Contains("aria-current=\"page\""), "the active tool card is current");
        await grid.SelectAsync("reviewer");
        check(selected == "reviewer", "the tool grid raises the card id");
    }
}
