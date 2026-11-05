using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Shell;

/// <summary>Light is the CodeConvert default. Dark matches a navy tool menu.</summary>
public enum ShellTheme
{
    Light,
    Dark
}

/// <summary>Built-in stroke icons. Hosts can supply <see cref="ShellNavItem.IconContent"/> instead.</summary>
public enum ShellIcon
{
    None,
    Home,
    Generator,
    Assistant,
    Explainer,
    Enhancer,
    Converter,
    Documentation,
    Test,
    Reviewer,
    Optimizer,
    Runner,
    Diagram,
    DiagramToCode,
    Chat,
    Comments,
    Files,
    History,
    Support,
    Account,
    Billing,
    Pin,
    Menu,
    Collapse,
    Chevron,
    Arrow
}

/// <summary>Progress of one wizard step rendered in the sidebar.</summary>
public enum ShellStepState
{
    Pending,
    Active,
    Done,
    Disabled
}

/// <summary>Raised when a sidebar item or wizard step is chosen.</summary>
public sealed record ShellNavigation(string Id, string? ParentId, int? StepNumber);

/// <summary>Number and state for a wizard step shown as a sidebar child.</summary>
public sealed class ShellWizardStep
{
    public int Number { get; init; }
    public ShellStepState State { get; init; } = ShellStepState.Pending;
}

/// <summary>One sidebar entry. Children are a nested group; set <see cref="Step"/> for a wizard step.</summary>
public sealed class ShellNavItem
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public ShellIcon Icon { get; init; }
    /// <summary>When set, the item renders as a link. Otherwise it raises <see cref="AppShell.ActiveItemIdChanged"/>.</summary>
    public string? Href { get; init; }
    public bool Disabled { get; init; }
    /// <summary>A parent with children can still open its own page. Wizard groups that only hold steps set this to false.</summary>
    public bool Selectable { get; init; } = true;
    public bool StartExpanded { get; init; } = true;
    public ShellWizardStep? Step { get; init; }
    public IReadOnlyList<ShellNavItem> Children { get; init; } = [];
    public RenderFragment? IconContent { get; init; }
}

/// <summary>A labelled block of sidebar items.</summary>
public sealed class ShellNavSection
{
    public string? Id { get; init; }
    public string? Title { get; init; }
    public IReadOnlyList<ShellNavItem> Items { get; init; } = [];
}

/// <summary>Plan summary pinned in the sidebar footer.</summary>
public sealed class ShellPlan
{
    public string Title { get; init; } = "Pro Plan";
    public string? Message { get; init; }
    public string ActionLabel { get; init; } = "Upgrade Plan";
    public string ActionId { get; init; } = "upgrade";
}

/// <summary>A "Get started" card: icon, description, and a text action.</summary>
public sealed class ShellStartCard
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string ActionLabel { get; init; }
    public ShellIcon Icon { get; init; }
}

/// <summary>
/// A pinned or popular tool tile. Set <see cref="SourceLanguageId"/> and
/// <see cref="TargetLanguageId"/> for a source-to-target tile, or
/// <see cref="LanguageId"/> for a single language.
/// </summary>
public sealed class ShellToolTile
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? LanguageId { get; init; }
    public string? SourceLanguageId { get; init; }
    public string? TargetLanguageId { get; init; }
    public bool ShowPin { get; init; } = true;
}

/// <summary>
/// One entry in the tool catalog. Hosts render the card grid and the sidebar
/// from this list. <see cref="Route"/> is an application path segment, not a URL.
/// </summary>
public sealed class ShellToolCard
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public ShellIcon Icon { get; init; }
    /// <summary>CSS color for the rounded icon square.</summary>
    public required string Color { get; init; }
    public required string Route { get; init; }
}

/// <summary>Factories for sidebar items and wizard steps.</summary>
public static class ShellNav
{
    public static ShellNavItem Item(string id, string label, ShellIcon icon) => new()
    {
        Id = id,
        Label = label,
        Icon = icon
    };

    public static ShellNavSection Section(string? title, params ShellNavItem[] items) => new()
    {
        Title = title,
        Items = items
    };
}

/// <summary>Builds the nested wizard group a migration flow shows in the sidebar.</summary>
public static class ShellWizard
{
    public static ShellNavItem Group(string id, string label, ShellIcon icon, params ShellNavItem[] steps) => new()
    {
        Id = id,
        Label = label,
        Icon = icon,
        Selectable = false,
        StartExpanded = true,
        Children = steps
    };

    public static ShellNavItem Step(int number, string id, string label, ShellStepState state = ShellStepState.Pending) => new()
    {
        Id = id,
        Label = label,
        Disabled = state == ShellStepState.Disabled,
        Selectable = state != ShellStepState.Disabled,
        Step = new ShellWizardStep { Number = number, State = state }
    };
}

/// <summary>
/// The CodeConvert-style tool menu: id, title, description, icon, color, and route.
/// <see cref="Menu"/> is the grid order from the tools menu. Comment Generator follows
/// that set so hosts that only render <see cref="Menu"/> still offer it.
/// </summary>
public static class ShellToolCatalog
{
    public static ShellToolCard Generator { get; } = Card("generator", "Code Generator", "Generate code snippets easily and efficiently.", ShellIcon.Generator, "#3b5bdb", "generator");
    public static ShellToolCard Assistant { get; } = Card("assistant", "Code Assistant", "AI-driven coding assistance on demand.", ShellIcon.Assistant, "#7048e8", "assistant");
    public static ShellToolCard Explainer { get; } = Card("explainer", "Code Explainer", "Understand complex code with clear explanations.", ShellIcon.Explainer, "#b08900", "explainer");
    public static ShellToolCard Enhancer { get; } = Card("enhancer", "Code Enhancer", "Improve code quality with smart suggestions.", ShellIcon.Enhancer, "#0ca678", "enhancer");
    public static ShellToolCard Converter { get; } = Card("converter", "Code Converter", "Convert code between languages effortlessly.", ShellIcon.Converter, "#e8590c", "converter");
    public static ShellToolCard Documentation { get; } = Card("documentation", "Documentation Generator", "Docstrings, READMEs and API docs for whole projects.", ShellIcon.Documentation, "#2f9e44", "documentation");
    public static ShellToolCard UnitTests { get; } = Card("unit-tests", "Unit Test Generator", "Create unit tests to ensure code quality.", ShellIcon.Test, "#c92a2a", "unit-tests");
    public static ShellToolCard Reviewer { get; } = Card("reviewer", "Code Reviewer", "Find bugs and security issues with an expert AI review.", ShellIcon.Reviewer, "#1c7ed6", "reviewer");
    public static ShellToolCard Optimizer { get; } = Card("optimizer", "Code Optimizer", "Make your code faster and leaner in one click.", ShellIcon.Optimizer, "#b45309", "optimizer");
    public static ShellToolCard Runner { get; } = Card("runner", "Code Runner", "Run code online in a fast sandbox, with one-click AI help.", ShellIcon.Runner, "#1c7ed6", "runner");
    public static ShellToolCard Diagram { get; } = Card("diagram", "Diagram Generator", "Generate visual diagrams from your code.", ShellIcon.Diagram, "#7048e8", "diagram");
    public static ShellToolCard DiagramToCode { get; } = Card("diagram-to-code", "Diagram to Code", "Convert diagrams into working code.", ShellIcon.DiagramToCode, "#a61e4d", "diagram-to-code");
    public static ShellToolCard Chat { get; } = Card("chat", "New Chat", "Chat with AI to solve any coding problem.", ShellIcon.Chat, "#0c8599", "chat");
    public static ShellToolCard Comments { get; } = Card("comments", "Comment Generator", "Add docstrings and comments in the language's idiom.", ShellIcon.Comments, "#2b8a3e", "comments");

    /// <summary>Grid order matching the tools menu, then Comment Generator.</summary>
    public static IReadOnlyList<ShellToolCard> Menu { get; } =
    [
        Generator, Assistant, Explainer, Enhancer, Converter, Documentation,
        UnitTests, Reviewer, Optimizer, Runner, Diagram, DiagramToCode, Chat, Comments
    ];

    public static IReadOnlyList<ShellStartCard> GetStarted { get; } =
    [
        new()
        {
            Id = Converter.Id,
            Title = "Code Converter",
            Description = "Seamlessly transform code across languages and frameworks with AI.",
            ActionLabel = "Convert Code Now",
            Icon = ShellIcon.Converter
        },
        new()
        {
            Id = Explainer.Id,
            Title = "Code Explainer",
            Description = "Understand complex code snippets with clear, AI-powered explanations.",
            ActionLabel = "Get Code Explanation",
            Icon = ShellIcon.Explainer
        },
        new()
        {
            Id = Generator.Id,
            Title = "Code Generator",
            Description = "Generate clean, efficient code tailored to your needs in seconds.",
            ActionLabel = "Generate Code",
            Icon = ShellIcon.Generator
        }
    ];

    public static ShellToolCard? Find(string? id) =>
        Menu.FirstOrDefault(card => string.Equals(card.Id, id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(card.Route, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Sidebar items for <see cref="Menu"/>. Pass a converter item to nest wizard steps under Code Converter.</summary>
    public static IReadOnlyList<ShellNavItem> ToNavItems(ShellNavItem? converterItem = null) =>
        Menu.Select(card => card.Id == Converter.Id && converterItem is not null
            ? converterItem
            : ShellNav.Item(card.Id, card.Title, card.Icon)).ToList();

    /// <summary>
    /// Home, the tool menu, and the lower library items (Files, History, Contact Support).
    /// The plan card and account links stay in the shell footer.
    /// </summary>
    public static IReadOnlyList<ShellNavSection> CreateSidebar(ShellNavItem? converterItem = null) =>
    [
        new ShellNavSection
        {
            Id = "primary",
            Items = new[] { ShellNav.Item("home", "Home", ShellIcon.Home) }.Concat(ToNavItems(converterItem)).ToList()
        },
        new ShellNavSection
        {
            Id = "library",
            Items =
            [
                ShellNav.Item("files", "Files", ShellIcon.Files),
                ShellNav.Item("history", "History", ShellIcon.History),
                ShellNav.Item("support", "Contact Support", ShellIcon.Support)
            ]
        }
    ];

    /// <summary>Code Converter as a selectable parent with wizard steps underneath.</summary>
    public static ShellNavItem ConverterWithSteps(params ShellNavItem[] steps) => new()
    {
        Id = Converter.Id,
        Label = Converter.Title,
        Icon = Converter.Icon,
        Selectable = true,
        StartExpanded = true,
        Children = steps
    };

    private static ShellToolCard Card(string id, string title, string description, ShellIcon icon, string color, string route) => new()
    {
        Id = id,
        Title = title,
        Description = description,
        Icon = icon,
        Color = color,
        Route = route
    };
}

/// <summary>A language monogram used on tool tiles.</summary>
public sealed record ShellLanguage(string Id, string Label, string Abbreviation, string Primary, string Secondary, params string[] Aliases)
{
    public bool Matches(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (text.Equals(Id, StringComparison.OrdinalIgnoreCase) || text.Equals(Label, StringComparison.OrdinalIgnoreCase))
            return true;
        return Aliases.Any(alias => alias.Equals(text, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Language marks for source-to-target tiles. Ids line up with <c>CodeLanguages</c>.</summary>
public static class ShellLanguages
{
    public static ShellLanguage Python { get; } = new("python", "Python", "Py", "#3572A5", "#FFD43B", "py");
    public static ShellLanguage C { get; } = new("c", "C", "C", "#555555", "#A8B9CC");
    public static ShellLanguage CSharp { get; } = new("csharp", "C#", "C#", "#68217A", "#9B4F96", "cs", "c#");
    public static ShellLanguage Ruby { get; } = new("ruby", "Ruby", "Rb", "#CC342D", "#FFFFFF", "rb");
    public static ShellLanguage Clojure { get; } = new("clojure", "Clojure", "Clj", "#5881D8", "#63B132", "clj");
    public static ShellLanguage JavaScript { get; } = new("javascript", "JavaScript", "Js", "#F7DF1E", "#323330", "js");
    public static ShellLanguage TypeScript { get; } = new("typescript", "TypeScript", "Ts", "#3178C6", "#FFFFFF", "ts");
    public static ShellLanguage Java { get; } = new("java", "Java", "Ja", "#EA2D2E", "#5382A1");
    public static ShellLanguage Go { get; } = new("go", "Go", "Go", "#00ADD8", "#FFFFFF", "golang");
    public static ShellLanguage Rust { get; } = new("rust", "Rust", "Rs", "#DEA584", "#000000", "rs");
    public static ShellLanguage Cpp { get; } = new("cpp", "C++", "C++", "#00599C", "#004482", "c++");

    public static IReadOnlyList<ShellLanguage> All { get; } =
        [Python, C, CSharp, Ruby, Clojure, JavaScript, TypeScript, Java, Go, Rust, Cpp];

    public static ShellLanguage? Find(string? id) => All.FirstOrDefault(language => language.Matches(id));
}

/// <summary>Inner SVG for <see cref="ShellIconView"/>. Stroke icons, 24 viewBox.</summary>
public static class ShellIconPaths
{
    public static string Markup(ShellIcon icon) => icon switch
    {
        ShellIcon.Home => """<path d="M4 10.5 12 4l8 6.5V20a1 1 0 0 1-1 1h-5v-6H10v6H5a1 1 0 0 1-1-1z"/>""",
        ShellIcon.Generator => """<path d="M8 8 4 12l4 4"/><path d="m16 8 4 4-4 4"/><path d="m14 5-4 14"/>""",
        ShellIcon.Assistant => """<rect x="5" y="8" width="14" height="9" rx="3"/><path d="M12 8V5"/><circle cx="12" cy="4" r="1"/><circle cx="9" cy="12" r=".8" fill="currentColor"/><circle cx="15" cy="12" r=".8" fill="currentColor"/><path d="M9 15h6"/>""",
        ShellIcon.Explainer => """<path d="M9 18h6M10 21h4"/><path d="M12 3a6 6 0 0 0-3.2 11.1c.5.5 1.2 1.2 1.2 2.1h4c0-.9.6-1.6 1.2-2.1A6 6 0 0 0 12 3z"/>""",
        ShellIcon.Enhancer => """<path d="M12 2.5v3M12 18.5v3M3.5 12h3M17.5 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M18.4 5.6l-2.1 2.1M7.7 16.3l-2.1 2.1"/><circle cx="12" cy="12" r="3"/>""",
        ShellIcon.Converter => """<path d="M7 7h11l-3.2-3.2"/><path d="M17 17H6l3.2 3.2"/>""",
        ShellIcon.Documentation => """<path d="M7 3.5h7.5L19 8v12.5a1 1 0 0 1-1 1H7a1 1 0 0 1-1-1v-16a1 1 0 0 1 1-1z"/><path d="M14.5 3.5V8H19M8 13h8M8 17h5"/>""",
        ShellIcon.Test => """<path d="M9 3h6M10 3v5.2L6.2 16a3.8 3.8 0 0 0 3.4 5.5h4.8A3.8 3.8 0 0 0 17.8 16L14 8.2V3"/><path d="M8.2 14h7.6"/>""",
        ShellIcon.Reviewer => """<circle cx="11" cy="11" r="6"/><path d="m20 20-3.6-3.6"/>""",
        ShellIcon.Optimizer => """<path d="M4.5 18.5a8.5 8.5 0 1 1 15 0"/><path d="M12 14 16.2 9"/><circle cx="12" cy="14" r="1.3" fill="currentColor"/>""",
        ShellIcon.Runner => """<circle cx="12" cy="12" r="8"/><path d="m10.5 9 5.5 3-5.5 3z" fill="currentColor"/>""",
        ShellIcon.Diagram => """<rect x="3" y="3" width="6" height="5" rx="1"/><rect x="15" y="3" width="6" height="5" rx="1"/><rect x="9" y="16" width="6" height="5" rx="1"/><path d="M6 8v2.5A2 2 0 0 0 8 12.5h8a2 2 0 0 0 2-2V8M12 12.5V16"/>""",
        ShellIcon.DiagramToCode => """<rect x="3" y="4" width="12" height="10" rx="1.5"/><path d="M3 8h12"/><circle cx="5.5" cy="6" r=".6" fill="currentColor"/><path d="m15 16 2 2 4-4"/>""",
        ShellIcon.Chat => """<path d="M6 16.8A6.8 6.8 0 1 1 8.6 19L5 20.2z"/><path d="M9 11h.01M12 11h.01M15 11h.01"/>""",
        ShellIcon.Comments => """<path d="M5 6.5h14v8.5H8.5L5 18z"/>""",
        ShellIcon.Files => """<path d="M4 7.5A2 2 0 0 1 6 5.5h3.5l2 2H18a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2z"/>""",
        ShellIcon.History => """<circle cx="12" cy="12" r="8"/><path d="M12 8v4.5l2.8 2"/>""",
        ShellIcon.Support => """<circle cx="12" cy="12" r="8"/><circle cx="12" cy="12" r="3"/><path d="M12 4v1.5M12 18.5V20M4 12h1.5M18.5 12H20"/>""",
        ShellIcon.Account => """<circle cx="12" cy="9" r="3"/><path d="M6.2 19a5.8 5.8 0 0 1 11.6 0"/>""",
        ShellIcon.Billing => """<rect x="3" y="6" width="18" height="12" rx="2"/><path d="M3 10h18"/>""",
        ShellIcon.Pin => """<path d="M9 3.5h6l-1 6.2 3 2.8v1.2H7v-1.2l3-2.8zM12 13.7V20"/>""",
        ShellIcon.Menu => """<path d="M4 7h16M4 12h16M4 17h16"/>""",
        ShellIcon.Collapse => """<rect x="4" y="4" width="16" height="16" rx="2"/><path d="M9 4v16"/>""",
        ShellIcon.Chevron => """<path d="m6 9 6 6 6-6"/>""",
        ShellIcon.Arrow => """<path d="M4 12h15M13 6l6 6-6 6"/>""",
        _ => ""
    };
}
