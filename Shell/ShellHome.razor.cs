using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Shell;

/// <summary>Home dashboard: Get started cards plus pinned and popular language tiles.</summary>
public partial class ShellHome
{
    [Parameter] public string Title { get; set; } = "Home";
    [Parameter] public string? Subtitle { get; set; }
    [Parameter] public string GetStartedTitle { get; set; } = "Get started";
    [Parameter] public IReadOnlyList<ShellStartCard> Cards { get; set; } = [];
    [Parameter] public string PinnedTitle { get; set; } = "Pinned Tools";
    [Parameter] public string? PinnedSubtitle { get; set; }
    [Parameter] public IReadOnlyList<ShellToolTile> Pinned { get; set; } = [];
    [Parameter] public string PopularTitle { get; set; } = "Popular Tools";
    [Parameter] public string? PopularSubtitle { get; set; }
    [Parameter] public IReadOnlyList<ShellToolTile> Popular { get; set; } = [];
    [Parameter] public EventCallback<string> OnCardAction { get; set; }
    [Parameter] public EventCallback<string> OnTileSelected { get; set; }

    private IReadOnlyList<ShellStartCard> CardList => Cards ?? [];
    private IReadOnlyList<ShellToolTile> PinnedList => Pinned ?? [];
    private IReadOnlyList<ShellToolTile> PopularList => Popular ?? [];

    public Task ActivateCardAsync(string id) => OnCardAction.InvokeAsync(id);

    public Task ActivateTileAsync(string id) => OnTileSelected.InvokeAsync(id);
}
