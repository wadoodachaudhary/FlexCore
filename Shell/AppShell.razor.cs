using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Shell;

/// <summary>
/// CodeConvert-style application shell: a collapsible sidebar, optional wizard
/// steps, a footer, and a main column. Navigation state stays with the host.
/// Set <see cref="Theme"/> to <see cref="ShellTheme.Dark"/> for the navy tool menu.
/// Colors come from <c>--fx-shell-*</c> variables; <see cref="Accent"/> overrides the green.
/// </summary>
public partial class AppShell
{
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.Ordinal);
    private readonly HashSet<string> _openedGroups = new(StringComparer.Ordinal);
    private bool _mobileOpen;

    [Parameter] public string Brand { get; set; } = "CodeConvert";
    [Parameter] public string? WorkspaceLabel { get; set; } = "Your Workspace";
    [Parameter] public string? WorkspaceName { get; set; }
    [Parameter] public EventCallback OnWorkspace { get; set; }
    [Parameter] public IReadOnlyList<ShellNavSection> Sections { get; set; } = [];
    [Parameter] public IReadOnlyList<ShellNavItem> FooterItems { get; set; } = [];
    [Parameter] public ShellPlan? Plan { get; set; }
    [Parameter] public EventCallback<string> OnPlanAction { get; set; }
    [Parameter] public RenderFragment? Banner { get; set; }
    [Parameter] public RenderFragment? Footer { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public string? ActiveItemId { get; set; }
    [Parameter] public EventCallback<string?> ActiveItemIdChanged { get; set; }
    [Parameter] public EventCallback<ShellNavigation> OnNavigate { get; set; }
    [Parameter] public bool Collapsed { get; set; }
    [Parameter] public EventCallback<bool> CollapsedChanged { get; set; }
    [Parameter] public ShellTheme Theme { get; set; } = ShellTheme.Light;
    [Parameter] public string? Accent { get; set; }
    [Parameter] public string SidebarAriaLabel { get; set; } = "Primary";

    protected override string? BaseCssClass
    {
        get
        {
            var classes = "fx-app-shell";
            if (Collapsed) classes += " is-collapsed";
            if (_mobileOpen) classes += " is-mobile-open";
            return classes;
        }
    }

    private string ThemeName => Theme == ShellTheme.Dark ? "dark" : "light";

    private IReadOnlyList<ShellNavSection> SectionList => Sections ?? [];

    private string WorkspaceInitial
    {
        get
        {
            var name = (WorkspaceName ?? Brand ?? "?").Trim();
            return name.Length == 0 ? "?" : char.ToUpperInvariant(name[0]).ToString();
        }
    }

    private string? RootStyle
    {
        get
        {
            var accent = string.IsNullOrWhiteSpace(Accent)
                ? null
                : "--fx-shell-accent:" + Accent.Trim() + ";--fx-shell-accent-strong:" + Accent.Trim() + ";";
            if (string.IsNullOrEmpty(accent)) return ResolvedStyle;
            if (string.IsNullOrEmpty(ResolvedStyle)) return accent;
            return accent + ResolvedStyle;
        }
    }

    /// <summary>Selects an item or step. Disabled entries are ignored. A non-selectable group only expands.</summary>
    public async Task SelectAsync(string id)
    {
        var found = Find(id);
        if (found is null) return;
        var (item, parent) = found.Value;
        if (IsItemDisabled(item)) return;
        if (!item.Selectable && item.Children.Count > 0)
        {
            await ToggleGroupAsync(item.Id);
            return;
        }

        if (item.Children.Count > 0)
            SetExpanded(item.Id, !IsExpanded(item) || item.Id != ActiveItemId);

        ActiveItemId = id;
        await ActiveItemIdChanged.InvokeAsync(id);
        await OnNavigate.InvokeAsync(new ShellNavigation(id, parent?.Id, item.Step?.Number));
        await InvokeAsync(StateHasChanged);
    }

    public async Task SetCollapsedAsync(bool collapsed)
    {
        Collapsed = collapsed;
        await CollapsedChanged.InvokeAsync(collapsed);
        await InvokeAsync(StateHasChanged);
    }

    public Task ToggleCollapsedAsync() => SetCollapsedAsync(!Collapsed);

    public async Task ToggleGroupAsync(string id)
    {
        var item = FindItem(id);
        if (item is null) return;
        SetExpanded(id, !IsExpanded(item));
        await InvokeAsync(StateHasChanged);
    }

    public Task ToggleMobileAsync()
    {
        _mobileOpen = !_mobileOpen;
        return InvokeAsync(StateHasChanged);
    }

    private async Task OnWorkspaceAsync() => await OnWorkspace.InvokeAsync();

    private async Task OnPlanAsync()
    {
        if (Plan is null) return;
        await OnPlanAction.InvokeAsync(Plan.ActionId);
    }

    private void SetExpanded(string id, bool expanded)
    {
        if (expanded)
        {
            _collapsedGroups.Remove(id);
            _openedGroups.Add(id);
        }
        else
        {
            _openedGroups.Remove(id);
            _collapsedGroups.Add(id);
        }
    }

    internal bool IsExpanded(ShellNavItem item)
    {
        if (item.Children.Count == 0 || Collapsed) return false;
        if (_collapsedGroups.Contains(item.Id)) return false;
        if (_openedGroups.Contains(item.Id)) return true;
        if (item.Children.Any(child => child.Id == ActiveItemId || child.Children.Any(grand => grand.Id == ActiveItemId)))
            return true;
        return item.StartExpanded;
    }

    internal bool IsActive(ShellNavItem item) =>
        string.Equals(item.Id, ActiveItemId, StringComparison.Ordinal);

    internal static bool IsItemDisabled(ShellNavItem item) =>
        item.Disabled || item.Step?.State == ShellStepState.Disabled;

    private ShellNavItem? FindItem(string id) => Find(id)?.Item;

    private (ShellNavItem Item, ShellNavItem? Parent)? Find(string id)
    {
        foreach (var section in SectionList)
        {
            var found = FindIn(section.Items, null, id);
            if (found is not null) return found;
        }

        if (FooterItems is not null)
        {
            var footer = FindIn(FooterItems, null, id);
            if (footer is not null) return footer;
        }

        return null;
    }

    private static (ShellNavItem Item, ShellNavItem? Parent)? FindIn(IReadOnlyList<ShellNavItem> items, ShellNavItem? parent, string id)
    {
        foreach (var item in items)
        {
            if (string.Equals(item.Id, id, StringComparison.Ordinal)) return (item, parent);
            if (item.Children.Count > 0)
            {
                var nested = FindIn(item.Children, item, id);
                if (nested is not null) return nested;
            }
        }

        return null;
    }
}
