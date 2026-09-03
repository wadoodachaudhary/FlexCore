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

    /// <summary>Open the popup as soon as the control renders with this true.
    /// Grid/tree cell editors pass the host's double-click-race flag here (see
    /// TreeGridCellEditContext.OpenOnRender): the flag may flip true on a LATER
    /// render than the mount, so it is checked every render and latched once.</summary>
    [Parameter] public bool OpenOnRender { get; set; }

    /// <summary>Focus the control on first render so keyboard dismissal (Escape)
    /// works immediately — required when hosted as an in-cell editor.</summary>
    [Parameter] public bool AutoFocus { get; set; }

    /// <summary>Raised when the popup closes (pick, outside click, toggle,
    /// clear, Escape) — and also on Escape while the popup is already closed,
    /// so a hosting cell editor can dismiss itself. On a pick,
    /// <see cref="ValueChanged"/> fires first, then this.</summary>
    [Parameter] public EventCallback Closed { get; set; }

    /// <summary>Hide the popup grid's header row (legacy multi-column combos
    /// render bare rows). Widths are preserved — the header collapses via CSS
    /// rather than leaving the DOM.</summary>
    [Parameter] public bool ShowColumnHeaders { get; set; } = true;

    /// <summary>Field shown in the CLOSED input. A grid combo has many columns,
    /// so the closed box shows the data/key column (e.g. "V20"), not a
    /// concatenation — set this to that field (owner directive 2026-09-02).
    /// Unset falls back to <see cref="TextFieldName"/>.</summary>
    [Parameter] public string? ClosedTextFieldName { get; set; }

    private ElementReference _containerRef;
    private ElementReference _inputRef;
    private GridControl<TItem>? _gridRef;
    // Row picks arrive through GridControl's EventsRef contract (the control
    // has no OnRowSelected parameter — the original wiring here was inert).
    private readonly GridControlEvents<TItem> _gridEvents = new();
    private bool _isOpen;
    private string _searchText = "";
    private bool _openOnRenderInitialized;
    private bool _autoFocused;
    // Press-witness (same rule as DropDownListControl): when this control mounts
    // MID-PRESS as a cell editor, the creating click can hit-test onto the input
    // wrap — without the witness that first click would instant-open the popup
    // and break the two-click contract. Only presses that put their mousedown on
    // the control may open it.
    private bool _pressStartedOnControl;

    protected override void OnInitialized()
    {
        _gridEvents.RowSelected = EventCallback.Factory.Create<RowSelectEventArgs<TItem>>(
            this, args => OnGridRowSelected(args.Data));
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (OpenOnRender && !_openOnRenderInitialized)
        {
            _openOnRenderInitialized = true;
            if (!_isOpen && !Disabled && !ReadOnly)
            {
                SeedKeyboardIndex();
                _isOpen = true;
                StateHasChanged();
            }
        }
        // The popup grid mounts a render after the open — apply the pending
        // keyboard highlight once it exists, then RETAKE focus: the reveal
        // scroll can land focus on the popup grid, which would route the next
        // arrow keys to the host tree instead of this combo.
        if (_pendingKeyboardHighlight && _isOpen && _gridRef != null)
        {
            _pendingKeyboardHighlight = false;
            await HighlightKeyboardIndexAsync();
            try { await _inputRef.FocusAsync(); } catch { /* disposed mid-focus */ }
        }
        if (AutoFocus && !_autoFocused && firstRender)
        {
            _autoFocused = true;
            try { await _inputRef.FocusAsync(); } catch { /* disposed mid-focus */ }
        }
    }

    private string ComputedContainerStyle => $"width: {Width ?? "100%"}; position: relative;";
    // z-index pairs with the fixed backdrop (9998) — same stacking scheme as
    // DropDownListControl's panel, so the popup overlays grid/tree chrome.
    private string ComputedPopupStyle => $"width: {PopupWidth ?? "450px"}; position: absolute; top: 100%; left: 0; z-index: 9999; background: #fff; border: 1px solid #ccc; border-radius: 4px;";

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
                return GetItemFieldText(selectedItem, ClosedTextFieldName ?? TextFieldName);
            }
            return Value.ToString() ?? "";
        }
    }

    private void HandleInputWrapMouseDown() => _pressStartedOnControl = true;

    private async Task ToggleDropdown()
    {
        if (Disabled || ReadOnly) return;
        var pressStartedHere = _pressStartedOnControl;
        _pressStartedOnControl = false;
        if (_isOpen)
        {
            await ClosePopupAsync();
            return;
        }
        if (!pressStartedHere) return;   // mid-press mount retarget — see field note
        SeedKeyboardIndex();
        _isOpen = true;
    }

    /// <summary>Closes the popup (if open) and raises <see cref="Closed"/>.</summary>
    private async Task ClosePopupAsync()
    {
        if (!_isOpen) return;
        _isOpen = false;
        _searchText = "";
        _keyboardIndex = -1;
        _pendingKeyboardHighlight = false;
        if (Closed.HasDelegate) await Closed.InvokeAsync();
    }

    // ── Keyboard navigation (owner directive 2026-09-02): Down opens the
    // list / moves the highlight, Up moves it back, Enter picks (or opens
    // when closed), Escape closes/dismisses. The highlight rides GridControl's
    // programmatic selection (SelectAndRevealRowAsync fires no RowSelected, so
    // arrowing never commits). Navigation order = DataSource order; the popup
    // search filter is not consulted (in-cell consumers run ShowSearch=false).
    private int _keyboardIndex = -1;
    private bool _pendingKeyboardHighlight;

    private List<TItem> KeyboardItems => (DataSource ?? Enumerable.Empty<TItem>()).ToList();

    private async Task HandleKeyDown(KeyboardEventArgs e)
    {
        switch (e.Key)
        {
            case "Escape":
                if (_isOpen)
                    await ClosePopupAsync();
                else if (Closed.HasDelegate)
                    await Closed.InvokeAsync();   // dismiss a closed in-cell editor
                break;

            case "ArrowDown":
            case "Down":
                if (!_isOpen) OpenFromKeyboard();
                else await MoveKeyboardHighlightAsync(1);
                break;

            case "ArrowUp":
            case "Up":
                if (_isOpen) await MoveKeyboardHighlightAsync(-1);
                break;

            case "Enter":
            case "NumpadEnter":
                if (!_isOpen)
                {
                    OpenFromKeyboard();
                }
                else
                {
                    var items = KeyboardItems;
                    if (_keyboardIndex >= 0 && _keyboardIndex < items.Count)
                        await OnGridRowSelected(items[_keyboardIndex]);
                }
                break;
        }
    }

    private void OpenFromKeyboard()
    {
        if (Disabled || ReadOnly) return;
        SeedKeyboardIndex();
        _isOpen = true;
    }

    /// <summary>Start the highlight at the current value (else the first row).</summary>
    private void SeedKeyboardIndex()
    {
        var items = KeyboardItems;
        _keyboardIndex = items.FindIndex(it =>
        {
            var v = GetItemValue(it);
            return EqualityComparer<TValue?>.Default.Equals(v, Value) || v?.ToString() == Value?.ToString();
        });
        if (_keyboardIndex < 0 && items.Count > 0) _keyboardIndex = 0;
        _pendingKeyboardHighlight = true;
    }

    private async Task MoveKeyboardHighlightAsync(int delta)
    {
        var items = KeyboardItems;
        if (items.Count == 0) return;
        _keyboardIndex = Math.Clamp((_keyboardIndex < 0 ? 0 : _keyboardIndex) + delta, 0, items.Count - 1);
        await HighlightKeyboardIndexAsync();
        try { await _inputRef.FocusAsync(); } catch { /* disposed mid-focus */ }
    }

    private async Task HighlightKeyboardIndexAsync()
    {
        var items = KeyboardItems;
        if (_gridRef == null || _keyboardIndex < 0 || _keyboardIndex >= items.Count) return;
        await _gridRef.SelectAndRevealRowAsync(items[_keyboardIndex], clearExistingSelection: true);
    }

    private async Task ClearValue()
    {
        Value = default;
        await ValueChanged.InvokeAsync(default);
        if (OnSelectedItemChanged.HasDelegate)
            await OnSelectedItemChanged.InvokeAsync(default);
        await ClosePopupAsync();
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
        await ClosePopupAsync();
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

    private string GetItemText(TItem item) => GetItemFieldText(item, TextFieldName);

    private static string GetItemFieldText(TItem item, string? fieldName)
    {
        if (item == null) return "";
        if (string.IsNullOrEmpty(fieldName)) return item.ToString() ?? "";

        var prop = typeof(TItem).GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return prop?.GetValue(item)?.ToString() ?? item.ToString() ?? "";
    }
}
