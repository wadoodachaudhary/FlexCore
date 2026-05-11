namespace Fx.ControlKit.Grid;

/// <summary>
/// Describes one column in the consumer's full layout schema for the Choose
/// Columns dialog. Lets a grid host (like FAssembly) feed in columns that
/// aren't currently rendered as <c>&lt;GridColumn&gt;</c> children — e.g. legacy
/// "30+ hidden columns" stored in the saved layout — without changing the
/// host's existing GetOrderedXxxGridLayout/render path.
/// </summary>
public sealed class ChooseColumnDescriptor
{
    /// <summary>Column field name. Used to identify the column in OnColumnsChosen.</summary>
    public string Field { get; init; } = "";

    /// <summary>User-facing header text shown in the dialog list.</summary>
    public string Header { get; init; } = "";

    /// <summary>Whether the column is currently visible in the grid.</summary>
    public bool Visible { get; init; }
}

/// <summary>Result payload passed to <c>OnColumnsChosen</c> when the user
/// clicks OK in the Choose Columns dialog. Lists every column from the
/// dialog's working set in the user's chosen order, with the user's chosen
/// visibility. The host applies these to its layout system.</summary>
public sealed class ChooseColumnsResult
{
    public IReadOnlyList<ChooseColumnDescriptor> Columns { get; init; } =
        Array.Empty<ChooseColumnDescriptor>();
}
