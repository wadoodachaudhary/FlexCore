using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fx.ControlKit.Grid;
using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit;

public sealed class FilterProperty
{
    public string Property { get; set; } = "";
    public string? Title { get; set; }
    public Type Type { get; set; } = typeof(string);
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
    [Parameter] public EventCallback<string> OnExpressionChanged { get; set; }
    [Parameter] public string? CssClass { get; set; }

    public FilterGroup RootGroup { get; private set; } = new();

    protected override void OnInitialized()
    {
        RootGroup = Value ?? new FilterGroup();
        if (RootGroup.Conditions.Count == 0 && Properties.Count > 0)
        {
            RootGroup.Conditions.Add(new FilterCondition
            {
                Field = Properties[0].Property,
                Operator = GridFilterOperator.Contains,
                Value = ""
            });
        }
    }

    private void SetGroupOperator(FilterGroup group, LogicalFilterOperator op)
    {
        group.LogicalOperator = op;
        NotifyChange();
    }

    private void AddCondition(FilterGroup group)
    {
        var defaultField = Properties.FirstOrDefault()?.Property ?? "";
        group.Conditions.Add(new FilterCondition
        {
            Field = defaultField,
            Operator = GridFilterOperator.Contains,
            Value = ""
        });
        NotifyChange();
    }

    private void RemoveCondition(FilterGroup group, FilterCondition condition)
    {
        group.Conditions.Remove(condition);
        NotifyChange();
    }

    private void AddSubGroup(FilterGroup group)
    {
        var defaultField = Properties.FirstOrDefault()?.Property ?? "";
        group.Groups.Add(new FilterGroup
        {
            LogicalOperator = LogicalFilterOperator.And,
            Conditions = new List<FilterCondition>
            {
                new() { Field = defaultField, Operator = GridFilterOperator.Contains, Value = "" }
            }
        });
        NotifyChange();
    }

    private void RemoveSubGroup(FilterGroup parentGroup, FilterGroup group)
    {
        parentGroup.Groups.Remove(group);
        NotifyChange();
    }

    private void OnConditionFieldChanged(FilterCondition condition, string? field)
    {
        condition.Field = field ?? "";
        NotifyChange();
    }

    private void OnConditionOperatorChanged(FilterCondition condition, string? opStr)
    {
        if (Enum.TryParse<GridFilterOperator>(opStr, out var op))
        {
            condition.Operator = op;
            NotifyChange();
        }
    }

    private void OnConditionValueChanged(FilterCondition condition, string? val)
    {
        condition.Value = val ?? "";
        NotifyChange();
    }

    private void NotifyChange()
    {
        var expr = BuildExpression(RootGroup);
        if (ValueChanged.HasDelegate)
            _ = ValueChanged.InvokeAsync(RootGroup);
        if (OnExpressionChanged.HasDelegate)
            _ = OnExpressionChanged.InvokeAsync(expr);
        StateHasChanged();
    }

    public string BuildExpression(FilterGroup group)
    {
        var parts = new List<string>();
        foreach (var c in group.Conditions.Where(c => !string.IsNullOrEmpty(c.Field)))
        {
            if (string.IsNullOrEmpty(c.Value) && c.Operator is not GridFilterOperator.IsNull and not GridFilterOperator.IsNotNull and not GridFilterOperator.IsEmpty and not GridFilterOperator.IsNotEmpty)
                continue;

            parts.Add(FormatCondition(c));
        }

        foreach (var g in group.Groups)
        {
            var sub = BuildExpression(g);
            if (!string.IsNullOrEmpty(sub))
                parts.Add($"({sub})");
        }

        if (parts.Count == 0) return "";
        var joiner = group.LogicalOperator == LogicalFilterOperator.And ? " AND " : " OR ";
        return string.Join(joiner, parts);
    }

    private string FormatCondition(FilterCondition c)
    {
        return c.Operator switch
        {
            GridFilterOperator.Equals => $"{c.Field} = \"{c.Value}\"",
            GridFilterOperator.NotEquals => $"{c.Field} != \"{c.Value}\"",
            GridFilterOperator.Contains => $"{c.Field} contains \"{c.Value}\"",
            GridFilterOperator.DoesNotContain => $"not ({c.Field} contains \"{c.Value}\")",
            GridFilterOperator.StartsWith => $"{c.Field} startswith \"{c.Value}\"",
            GridFilterOperator.EndsWith => $"{c.Field} endswith \"{c.Value}\"",
            GridFilterOperator.GreaterThan => $"{c.Field} > {c.Value}",
            GridFilterOperator.GreaterThanOrEquals => $"{c.Field} >= {c.Value}",
            GridFilterOperator.LessThan => $"{c.Field} < {c.Value}",
            GridFilterOperator.LessThanOrEquals => $"{c.Field} <= {c.Value}",
            GridFilterOperator.IsNull => $"{c.Field} is null",
            GridFilterOperator.IsNotNull => $"{c.Field} is not null",
            GridFilterOperator.IsEmpty => $"{c.Field} = \"\"",
            GridFilterOperator.IsNotEmpty => $"{c.Field} != \"\"",
            _ => $"{c.Field} contains \"{c.Value}\""
        };
    }
}
