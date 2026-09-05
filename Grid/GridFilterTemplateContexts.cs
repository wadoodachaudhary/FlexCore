namespace Fx.ControlKit.Grid;

/// <summary>One distinct value made available to a custom filter menu.</summary>
public sealed record GridFilterValueOption(string Value, string DisplayText, bool IsSelected);

/// <summary>Selection state for an Excel-style checklist's select-all control.</summary>
public enum GridFilterSelectionState
{
    None,
    Some,
    All
}

/// <summary>
/// Context for <see cref="GridColumn.FilterCellTemplate"/>. Value/operator
/// changes are staged; call <see cref="ApplyAsync"/> to commit them or
/// <see cref="ClearAsync"/> to remove the filter.
/// </summary>
public sealed class GridFilterCellTemplateContext
{
    public required GridColumn Column { get; init; }
    public string? Value { get; init; }
    public TextFilterOperator Operator { get; init; }
    public required IReadOnlyList<TextFilterOperator> Operators { get; init; }
    public required Func<object?, Task> ValueChanged { get; init; }
    public required Func<TextFilterOperator, Task> OperatorChanged { get; init; }
    public required Func<Task> ApplyAsync { get; init; }
    public required Func<Task> ClearAsync { get; init; }
}

/// <summary>
/// Context for <see cref="GridColumn.FilterMenuTemplate"/>. It exposes both
/// conditions, checklist values, and explicit apply/clear/close callbacks.
/// </summary>
public sealed class GridFilterMenuTemplateContext
{
    public required GridColumn Column { get; init; }
    public string? Value { get; init; }
    public TextFilterOperator Operator { get; init; }
    public string? SecondValue { get; init; }
    public TextFilterOperator SecondOperator { get; init; }
    public LogicalFilterOperator LogicalOperator { get; init; }
    public required IReadOnlyList<TextFilterOperator> Operators { get; init; }
    public required IReadOnlyList<GridFilterValueOption> DistinctValues { get; init; }
    /// <summary>
    /// Search text that selects matching checklist values. The selection is
    /// committed by Auto Apply or ApplyAsync, independently of the conditions.
    /// </summary>
    public string? ChecklistSearchText { get; init; }
    public GridFilterSelectionState SelectAllState { get; init; }
    public required Func<object?, Task> ValueChanged { get; init; }
    public required Func<TextFilterOperator, Task> OperatorChanged { get; init; }
    public required Func<object?, Task> SecondValueChanged { get; init; }
    public required Func<TextFilterOperator, Task> SecondOperatorChanged { get; init; }
    public required Func<LogicalFilterOperator, Task> LogicalOperatorChanged { get; init; }
    public Func<string?, Task> ChecklistSearchTextChanged { get; init; } = _ => Task.CompletedTask;
    public required Func<string, bool, Task> SetValueSelectedAsync { get; init; }
    public Func<bool, Task> SetAllVisibleValuesSelectedAsync { get; init; } = _ => Task.CompletedTask;
    public required Func<Task> ApplyAsync { get; init; }
    public required Func<Task> ClearAsync { get; init; }
    public required Func<Task> CloseAsync { get; init; }
}

/// <summary>Context for replacing the standard filter-menu action buttons.</summary>
public sealed class GridFilterMenuButtonsTemplateContext
{
    public required GridColumn Column { get; init; }
    public bool AutoApply { get; init; }
    public required Func<Task> ApplyAsync { get; init; }
    public required Func<Task> ClearAsync { get; init; }
    public required Func<Task> CloseAsync { get; init; }
}
