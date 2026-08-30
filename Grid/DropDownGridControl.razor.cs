using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Fx.ControlKit.Grid;

public partial class DropDownGridControl<TItem, TValue> : ComponentBase
{
    [Parameter] public IEnumerable<TItem>? DataSource { get; set; }
    [Parameter] public TValue? Value { get; set; }
    [Parameter] public EventCallback<TValue> ValueChanged { get; set; }
    [Parameter] public EventCallback<TItem?> OnSelectedItemChanged { get; set; }
    [Parameter] public string? TextFieldName { get; set; }
    [Parameter] public string? ValueFieldName { get; set; }
    [Parameter] public string? Placeholder { get; set; } = "Select an item...";
    [Parameter] public string? SearchPlaceholder { get; set; }
    [Parameter] public bool ShowSearch { get; set; } = true;
    [Parameter] public bool AllowClear { get; set; } = true;
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public bool ReadOnly { get; set; }
    [Parameter] public string? Width { get; set; } = "100%";
    [Parameter] public string? PopupWidth { get; set; } = "450px";
    [Parameter] public string? PopupHeight { get; set; } = "240px";
    [Parameter] public int RowHeight { get; set; } = 22;
    [Parameter] public bool AllowPaging { get; set; } = false;
    [Parameter] public int PageSize { get; set; } = 10;
    [Parameter] public string? CssClass { get; set; }
    [Parameter] public RenderFragment? Columns { get; set; }

    private ElementReference _containerRef;
    private GridControl<TItem>? _gridRef;
    private bool _isOpen;
    private string _searchText = "";

    private string ComputedContainerStyle => $"width: {Width ?? "100%"}; position: relative;";
    private string ComputedPopupStyle => $"width: {PopupWidth ?? "450px"}; position: absolute; top: 100%; left: 0; z-index: 1050; background: #fff; border: 1px solid #ccc; border-radius: 4px;";

    private bool HasValue => Value != null && !string.IsNullOrEmpty(Value.ToString());

    private IEnumerable<TItem> FilteredDataSource => DataSource ?? Enumerable.Empty<TItem>();

    private string DisplayText
    {
        get
        {
            if (Value == null || DataSource == null) return "";
            var selectedItem = DataSource.FirstOrDefault(item =>
            {
                var val = GetItemValue(item);
                return EqualityComparer<TValue>.Default.Equals(val, Value) || val?.ToString() == Value.ToString();
            });

            if (selectedItem != null)
            {
                return GetItemText(selectedItem);
            }
            return Value.ToString() ?? "";
        }
    }

    private void ToggleDropdown()
    {
        if (Disabled || ReadOnly) return;
        _isOpen = !_isOpen;
        if (!_isOpen)
        {
            _searchText = "";
        }
    }

    private async Task ClearValue()
    {
        Value = default;
        await ValueChanged.InvokeAsync(default);
        if (OnSelectedItemChanged.HasDelegate)
            await OnSelectedItemChanged.InvokeAsync(default);
        _isOpen = false;
    }

    private void OnSearchInput(ChangeEventArgs e)
    {
        _searchText = e.Value?.ToString() ?? "";
    }

    private async Task OnGridRowSelected(TItem? selectedItem)
    {
        if (selectedItem == null) return;
        var val = GetItemValue(selectedItem);
        Value = val;
        await ValueChanged.InvokeAsync(val);
        if (OnSelectedItemChanged.HasDelegate)
            await OnSelectedItemChanged.InvokeAsync(selectedItem);
        _isOpen = false;
        _searchText = "";
        StateHasChanged();
    }

    private TValue? GetItemValue(TItem item)
    {
        if (item == null) return default;
        if (string.IsNullOrEmpty(ValueFieldName))
        {
            if (item is TValue directVal) return directVal;
            return default;
        }

        var prop = typeof(TItem).GetProperty(ValueFieldName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop != null)
        {
            var raw = prop.GetValue(item);
            if (raw is TValue typed) return typed;
            if (raw != null)
            {
                try
                {
                    return (TValue)Convert.ChangeType(raw, typeof(TValue));
                }
                catch { }
            }
        }
        return default;
    }

    private string GetItemText(TItem item)
    {
        if (item == null) return "";
        if (string.IsNullOrEmpty(TextFieldName)) return item.ToString() ?? "";

        var prop = typeof(TItem).GetProperty(TextFieldName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return prop?.GetValue(item)?.ToString() ?? item.ToString() ?? "";
    }
}
