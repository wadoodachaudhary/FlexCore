using System.Globalization;

namespace Fx.ControlKit.Grid;

/// <summary>Which relatives to display around rows matching every column filter.</summary>
public enum TreeGridFilterHierarchyMode { None, Parent, Child, Both }

public sealed record TreeGridFilter(string Field, TextFilterOperator Operator, string? Value = null,
    TextFilterOperator SecondOperator = TextFilterOperator.Contains, string? SecondValue = null,
    LogicalFilterOperator LogicalOperator = LogicalFilterOperator.And);

/// <summary>Serializable view state. IDs use the mapped property's invariant string representation.</summary>
public sealed class TreeGridViewState
{
    public List<GridSortDescriptor> Sorts { get; set; } = [];
    public List<TreeGridFilter> Filters { get; set; } = [];
    public int FrozenColumns { get; set; }
    public Dictionary<string, FrozenColumnPosition?> FrozenPositions { get; set; } = [];
    public List<string> CheckedIds { get; set; } = [];
    public List<string> ExpandedIds { get; set; } = [];
    public string? SelectedId { get; set; }
    public TreeGridFilterHierarchyMode HierarchyMode { get; set; } = TreeGridFilterHierarchyMode.Both;
    public bool MatchCase { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public Dictionary<string, bool> ColumnVisibility { get; set; } = [];
    public Dictionary<string, double> ColumnWidths { get; set; } = [];
}

/// <summary>Typed comparisons shared by TreeGrid's menu and programmatic filters.</summary>
public static class TreeGridQuery
{
    public static bool Matches(object? value, ColumnType type, TreeGridFilter filter, bool matchCase = false)
    {
        var first = MatchesCondition(value, type, filter.Operator, filter.Value, matchCase);
        if (!IsActive(filter.SecondOperator, filter.SecondValue)) return first;
        var second = MatchesCondition(value, type, filter.SecondOperator, filter.SecondValue, matchCase);
        if (!IsActive(filter.Operator, filter.Value)) return second;
        return filter.LogicalOperator == LogicalFilterOperator.And ? first && second : first || second;
    }

    internal static bool IsActive(TextFilterOperator op, string? value) =>
        op is TextFilterOperator.IsEmpty or TextFilterOperator.IsNotEmpty || !string.IsNullOrEmpty(value);

    private static bool MatchesCondition(object? value, ColumnType type, TextFilterOperator op, string? expected, bool matchCase)
    {
        var text = Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
        if (op == TextFilterOperator.IsEmpty) return string.IsNullOrWhiteSpace(text);
        if (op == TextFilterOperator.IsNotEmpty) return !string.IsNullOrWhiteSpace(text);
        if (!IsActive(op, expected) || op == TextFilterOperator.ChooseOne) return true;
        expected ??= "";
        var comparison = matchCase ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase;
        switch (op)
        {
            case TextFilterOperator.Contains: return text.Contains(expected, comparison);
            case TextFilterOperator.DoesNotContain: return !text.Contains(expected, comparison);
            case TextFilterOperator.BeginsWith: return text.StartsWith(expected, comparison);
            case TextFilterOperator.DoesNotBeginWith: return !text.StartsWith(expected, comparison);
            case TextFilterOperator.EndsWith: return text.EndsWith(expected, comparison);
            case TextFilterOperator.DoesNotEndWith: return !text.EndsWith(expected, comparison);
        }
        int compare;
        if (type == ColumnType.Number)
        {
            if (value is null || !decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var a)
                || !decimal.TryParse(expected, NumberStyles.Number, CultureInfo.CurrentCulture, out var b)) return false;
            compare = a.CompareTo(b);
        }
        else if (type == ColumnType.Date)
        {
            if (value is null || !DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var a)
                || !DateTime.TryParse(expected, CultureInfo.CurrentCulture, DateTimeStyles.None, out var b)) return false;
            compare = a.Date.CompareTo(b.Date);
        }
        else if (type is ColumnType.Boolean or ColumnType.CheckBox)
        {
            if (!bool.TryParse(text, out var a) || !bool.TryParse(expected, out var b)) return false;
            compare = a.CompareTo(b);
        }
        else compare = string.Compare(text, expected, comparison);
        return op switch
        {
            TextFilterOperator.Equals => compare == 0,
            TextFilterOperator.DoesNotEqual => compare != 0,
            TextFilterOperator.GreaterThan => compare > 0,
            TextFilterOperator.GreaterThanOrEqual => compare >= 0,
            TextFilterOperator.LessThan => compare < 0,
            TextFilterOperator.LessThanOrEqual => compare <= 0,
            _ => true
        };
    }
}
