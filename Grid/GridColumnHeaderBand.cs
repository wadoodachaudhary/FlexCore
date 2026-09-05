using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Grid;

/// <summary>
/// Describes a one-level header band spanning adjacent visible grid columns.
/// Field matching is case-insensitive. When column reordering separates the
/// configured fields, or a frozen-column boundary divides them, the grid
/// renders one band cell for each contiguous positioning run rather than
/// spanning unrelated columns.
/// </summary>
public sealed class GridColumnHeaderBand
{
    public GridColumnHeaderBand()
    {
    }

    public GridColumnHeaderBand(string headerText, params string[] fields)
    {
        HeaderText = headerText;
        Fields = fields;
    }

    /// <summary>Text shown in the spanning parent header.</summary>
    public string HeaderText { get; set; } = string.Empty;

    /// <summary>Leaf-column fields owned by this band.</summary>
    public IReadOnlyList<string> Fields { get; set; } = Array.Empty<string>();

    /// <summary>Optional custom content for the band header.</summary>
    public RenderFragment? HeaderTemplate { get; set; }

    /// <summary>Optional CSS class appended to the band cell.</summary>
    public string? CssClass { get; set; }

    /// <summary>Horizontal alignment of the band caption.</summary>
    public TextAlign TextAlign { get; set; } = TextAlign.Center;
}
