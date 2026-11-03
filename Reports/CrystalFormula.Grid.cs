namespace Fx.ControlKit.Reports;

/// <summary>The summarized cross-tab, not the report's raw record stream.</summary>
public sealed class CrystalGridFormulaContext
{
    public int RowIndex { get; init; }
    public int ColumnIndex { get; init; }
    public int SummaryIndex { get; init; }
    public int RowCount { get; init; }
    public int ColumnCount { get; init; }
    public int SummaryCount { get; init; }
    public int RowGroupCount { get; init; }
    public int ColumnGroupCount { get; init; }
    public required Func<int, int, int, object?> ValueAt { get; init; }
    public required Func<bool, IReadOnlyList<object?>, int> PathIndex { get; init; }
    public required Func<string, int?, object?> GroupValue { get; init; }
    public required Func<string, int, object?> LabelAt { get; init; }
    public required Func<bool, int, int> GroupIndex { get; init; }
    public required Func<string, object?, int, int> IndexOf { get; init; }
    public required Func<string, int, object?> TotalValue { get; init; }
}

public sealed partial class CrystalFormula
{
    private static readonly HashSet<string> GridSymbols = new(("currentrowindex currentcolumnindex currentsummaryindex " +
        "getnumrows getnumcolumns getnumsummaries getnumrowgroups getnumcolumngroups").Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> GridFunctions = new(("gridvalueat gridrowcolumnvalue gridlabelat getrowpathindexof getcolumnpathindexof " +
        "getrowgroupindexof getcolumngroupindexof getindexof gettotalvaluefor " +
        "getnumrows getnumcolumns getnumsummaries getnumrowgroups getnumcolumngroups").Split(' '), StringComparer.OrdinalIgnoreCase);
    private static CrystalGridFormulaContext Grid(Frame frame) => frame.Context.Grid
        ?? throw new InvalidDataException("Crystal grid functions require a summarized cross-tab context.");
    private static object GridSymbol(Frame frame, string name)
    {
        var grid = Grid(frame);
        return name.ToLowerInvariant() switch
        {
            "currentrowindex" => grid.RowIndex, "currentcolumnindex" => grid.ColumnIndex, "currentsummaryindex" => grid.SummaryIndex,
            "getnumrows" => grid.RowCount, "getnumcolumns" => grid.ColumnCount, "getnumsummaries" => grid.SummaryCount,
            "getnumrowgroups" => grid.RowGroupCount, "getnumcolumngroups" => grid.ColumnGroupCount,
            _ => throw new InvalidDataException($"Unknown Crystal grid symbol '{name}'.")
        };
    }
    private static object? InvokeGrid(Frame frame, string name, object?[] args)
    {
        var grid = Grid(frame);
        var valid = name switch
        {
            "gridvalueat" or "getindexof" => args.Length == 3 || name == "getindexof" && args.Length == 2,
            "gridrowcolumnvalue" or "gettotalvaluefor" => args.Length is 1 or 2,
            "gridlabelat" => args.Length == 2,
            "getrowgroupindexof" or "getcolumngroupindexof" => args.Length == 1,
            "getrowpathindexof" or "getcolumnpathindexof" => true,
            _ => args.Length == 0
        };
        if (!valid) throw new InvalidDataException($"Invalid argument count for {name}.");
        int Index(int i)
        {
            var number = Number(args[i]);
            if (decimal.Truncate(number) != number) throw new InvalidDataException($"{name} requires integral grid indices.");
            return checked((int)number);
        }
        return name switch
        {
            "gridvalueat" => grid.ValueAt(Index(0), Index(1), Index(2)),
            "gridrowcolumnvalue" => grid.GroupValue(Text(args[0]), args.Length == 2 ? Index(1) : null),
            "gridlabelat" => grid.LabelAt(Text(args[0]), Index(1)),
            "getrowpathindexof" => grid.PathIndex(false, args), "getcolumnpathindexof" => grid.PathIndex(true, args),
            "getrowgroupindexof" => grid.GroupIndex(false, Index(0)), "getcolumngroupindexof" => grid.GroupIndex(true, Index(0)),
            "getindexof" => grid.IndexOf(Text(args[0]), args[1], args.Length == 3 ? Index(2) : 0),
            "gettotalvaluefor" => grid.TotalValue(Text(args[0]), args.Length == 2 ? Index(1) : grid.SummaryIndex),
            _ => GridSymbol(frame, name)
        };
    }
}
