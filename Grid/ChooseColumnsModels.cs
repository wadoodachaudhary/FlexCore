namespace Fx.ControlKit.Grid;

public sealed class ChooseColumnDescriptor
{
    public string Field { get; init; } = "";

    public string Header { get; init; } = "";

    public bool Visible { get; init; }
}

public sealed class ChooseColumnsResult
{
    public IReadOnlyList<ChooseColumnDescriptor> Columns { get; init; } =
        Array.Empty<ChooseColumnDescriptor>();
}
