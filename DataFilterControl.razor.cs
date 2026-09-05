using System.Globalization;
using System.Text.Json;
using Fx.ControlKit.Grid;
using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit;

public sealed class FilterProperty
{
    public string Property { get; set; } = "";
    public string? Title { get; set; }
    public string DisplayTitle => Title ?? Property;
    public Type Type { get; set; } = typeof(string);
    public RenderFragment<FilterValueTemplateContext>? ValueTemplate { get; set; }
}

public sealed class FilterValueTemplateContext
{
    public required FilterProperty Property { get; init; }
    public required FilterCondition Condition { get; init; }
    public required Func<string?, Task> SetValueAsync { get; init; }
}

public sealed class FilterCondition
{
    public string Field { get; set; } = "";
    public GridFilterOperator Operator { get; set; } = GridFilterOperator.Contains;
    public string Value { get; set; } = "";
}

public sealed class FilterGroup
{
    public LogicalFilterOperator LogicalOperator { get; set; } = LogicalFilterOperator.And;
    public List<FilterCondition> Conditions { get; set; } = new();
    public List<FilterGroup> Groups { get; set; } = new();
}

public partial class DataFilterControl : ComponentBase
{
    [Parameter] public List<FilterProperty> Properties { get; set; } = new();
    [Parameter] public FilterGroup? Value { get; set; }
    [Parameter] public EventCallback<FilterGroup> ValueChanged { get; set; }
    /// <summary>Human-readable expression text; use BuildPredicate for execution.</summary>
    [Parameter] public EventCallback<string> OnExpressionChanged { get; set; }
    [Parameter] public string? CssClass { get; set; }
    [Parameter] public CultureInfo Culture { get; set; } = CultureInfo.CurrentCulture;
    [Parameter] public bool CaseSensitive { get; set; }

    public FilterGroup RootGroup { get; private set; } = new();
    private FilterGroup? _lastValue;
    private bool _initialized;

    protected override void OnParametersSet()
    {
        if (!_initialized || !ReferenceEquals(Value, _lastValue))
        {
            RootGroup = Value ?? new FilterGroup();
            if (Value == null && Properties.Count > 0)
                RootGroup.Conditions.Add(NewCondition());
            _lastValue = Value;
            _initialized = true;
        }
    }

    /// <summary>Builds a snapshot predicate for local data. Invalid values throw FormatException.</summary>
    public Func<T, bool> BuildPredicate<T>() => FilterEvaluator.BuildPredicate<T>(RootGroup, Properties, Culture, CaseSensitive);

    private FilterProperty? PropertyFor(FilterCondition condition) => Properties.FirstOrDefault(p => p.Property == condition.Field);
    private Type ValueType(FilterCondition condition) => Nullable.GetUnderlyingType(PropertyFor(condition)?.Type ?? typeof(string))
        ?? PropertyFor(condition)?.Type ?? typeof(string);
    private FilterCondition NewCondition() => new()
    {
        Field = Properties.FirstOrDefault()?.Property ?? "",
        Operator = (Nullable.GetUnderlyingType(Properties.FirstOrDefault()?.Type ?? typeof(string))
            ?? Properties.FirstOrDefault()?.Type ?? typeof(string)) == typeof(string)
            ? GridFilterOperator.Contains : GridFilterOperator.Equals
    };

    private async Task SetGroupOperator(FilterGroup group, LogicalFilterOperator op)
    { group.LogicalOperator = op; await NotifyChangeAsync(); }
    private async Task AddCondition(FilterGroup group)
    { group.Conditions.Add(NewCondition()); await NotifyChangeAsync(); }
    private async Task RemoveCondition(FilterGroup group, FilterCondition condition)
    { group.Conditions.Remove(condition); await NotifyChangeAsync(); }
    private async Task AddSubGroup(FilterGroup group)
    { group.Groups.Add(new FilterGroup { Conditions = [NewCondition()] }); await NotifyChangeAsync(); }
    private async Task RemoveSubGroup(FilterGroup parent, FilterGroup group)
    { parent.Groups.Remove(group); await NotifyChangeAsync(); }
    private async Task OnConditionFieldChanged(FilterCondition condition, string? field)
    {
        condition.Field = field ?? "";
        condition.Value = "";
        condition.Operator = ValueType(condition) == typeof(string) ? GridFilterOperator.Contains : GridFilterOperator.Equals;
        await NotifyChangeAsync();
    }
    private async Task OnConditionOperatorChanged(FilterCondition condition, GridFilterOperator op)
    { condition.Operator = op; await NotifyChangeAsync(); }
    private async Task OnConditionValueChanged(FilterCondition condition, string? value)
    { condition.Value = value ?? ""; await NotifyChangeAsync(); }
    private async Task NotifyChangeAsync()
    {
        await ValueChanged.InvokeAsync(RootGroup);
        await OnExpressionChanged.InvokeAsync(BuildExpression(RootGroup));
    }

    private sealed record OperatorChoice(GridFilterOperator Value, string Label);
    private IEnumerable<OperatorChoice> OperatorsFor(FilterCondition condition) =>
        FilterEvaluator.GetOperators(PropertyFor(condition)?.Type ?? typeof(string))
            .Select(op => new OperatorChoice(op, op switch
            {
                GridFilterOperator.NotEquals => "Not Equals", GridFilterOperator.DoesNotContain => "Does Not Contain",
                GridFilterOperator.StartsWith => "Starts With", GridFilterOperator.EndsWith => "Ends With",
                GridFilterOperator.GreaterThan => "Greater Than", GridFilterOperator.GreaterThanOrEquals => "Greater Than or Equal",
                GridFilterOperator.LessThan => "Less Than", GridFilterOperator.LessThanOrEquals => "Less Than or Equal",
                GridFilterOperator.IsNull => "Is Null", GridFilterOperator.IsNotNull => "Is Not Null",
                GridFilterOperator.IsEmpty => "Is Empty", GridFilterOperator.IsNotEmpty => "Is Not Empty",
                _ => op.ToString()
            }));
    private IEnumerable<string> ChoicesFor(FilterCondition condition) => ValueType(condition) == typeof(bool)
        ? ["True", "False"] : Enum.GetNames(ValueType(condition));
    private DateTime? DateValue(FilterCondition condition) => DateTime.TryParse(condition.Value, Culture,
        DateTimeStyles.None, out var value) ? value : null;
    private Task OnDateValueChanged(FilterCondition condition, DateTime? value) =>
        OnConditionValueChanged(condition, value?.ToString(ValueType(condition) == typeof(DateOnly) ? "d" : "G", Culture));
    private string? ConditionError(FilterCondition condition)
    {
        try { FilterEvaluator.Validate(condition, Properties, Culture); return null; }
        catch (FormatException ex) { return ex.Message; }
    }

    /// <summary>Formats a display expression, not SQL or an executable query language.</summary>
    public string BuildExpression(FilterGroup group)
    {
        var parts = group.Conditions.Where(FilterEvaluator.IsActive).Select(FormatCondition).ToList();
        parts.AddRange(group.Groups.Select(BuildExpression).Where(s => s.Length > 0).Select(s => $"({s})"));
        return string.Join(group.LogicalOperator == LogicalFilterOperator.And ? " AND " : " OR ", parts);
    }

    private static string FormatCondition(FilterCondition c)
    {
        // Keep the existing expression's operator spelling; escape quoted drafts.
        var quoted = JsonSerializer.Serialize(c.Value);
        return c.Operator switch
        {
            GridFilterOperator.Equals => $"{c.Field} = {quoted}",
            GridFilterOperator.NotEquals => $"{c.Field} != {quoted}",
            GridFilterOperator.Contains => $"{c.Field} contains {quoted}",
            GridFilterOperator.DoesNotContain => $"not ({c.Field} contains {quoted})",
            GridFilterOperator.StartsWith => $"{c.Field} startswith {quoted}",
            GridFilterOperator.EndsWith => $"{c.Field} endswith {quoted}",
            GridFilterOperator.GreaterThan => $"{c.Field} > {c.Value}",
            GridFilterOperator.GreaterThanOrEquals => $"{c.Field} >= {c.Value}",
            GridFilterOperator.LessThan => $"{c.Field} < {c.Value}",
            GridFilterOperator.LessThanOrEquals => $"{c.Field} <= {c.Value}",
            GridFilterOperator.IsNull => $"{c.Field} is null",
            GridFilterOperator.IsNotNull => $"{c.Field} is not null",
            GridFilterOperator.IsEmpty => $"{c.Field} = \"\"",
            GridFilterOperator.IsNotEmpty => $"{c.Field} != \"\"",
            _ => $"{c.Field} {c.Operator} {quoted}"
        };
    }

}
