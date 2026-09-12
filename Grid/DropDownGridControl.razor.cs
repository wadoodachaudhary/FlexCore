using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Fx.ControlKit.Grid;

public partial class DropDownGridControl<TItem, TValue> : ComponentBase, IAsyncDisposable
{
    [Inject] private IJSRuntime JSRuntime { get; set; } = default!;

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

    /// <summary>Set when this control is the editor of a hosted cell (a TreeGrid
    /// cell-edit template): the host decides focus, opening and the Tab stop, takes
    /// the keys the combo does not consume, and is told when the edit ends — the
    /// page wires nothing. Cursor phase: closed, no focus, no Tab stop; editing
    /// phase: focus on mount, opens when the host asks. (No type-select: a typed
    /// start opens the popup and the character is not searched.)</summary>
    [CascadingParameter] private ICellEditorHost? CellHost { get; set; }

    private bool EffectiveAutoFocus => CellHost is null ? AutoFocus : CellHost.IsEditing;
    private bool EffectiveOpenOnRender => CellHost is null
        ? OpenOnRender
        : CellHost.IsEditing && (OpenOnRender || CellHost.OpenOnRender || CellHost.InitialText is not null);



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
    private ElementReference _popupRef;
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

    // Popup placement. The popup is position:fixed so it escapes scroll containers
    // and clipped table cells; it stays hidden off-screen until measured, then sits
    // at the viewport coordinates FlexKit's shared dropdown helper returns (flips up
    // when there is no room below). If the module cannot load, it falls back to the
    // in-host absolute position so the list is never left invisible.
    private IJSObjectReference? _jsModule;
    private bool _popupPlaced;
    private bool _popupFallback;
    private double _popupTop;
    private double _popupLeft;

    private sealed class PopupGeometry
    {
        public bool OpenUp { get; set; }
        public double MaxHeight { get; set; }
        public double Top { get; set; }
        public double Left { get; set; }
    }

    private static double Px(string? value, double fallback) =>
        double.TryParse((value ?? "").Trim().Replace("px", "", StringComparison.OrdinalIgnoreCase),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : fallback;

    private async ValueTask<IJSObjectReference?> GetJsModuleAsync()
    {
        try
        {
            return _jsModule ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", $"./_content/{typeof(DropDownGridControl<TItem, TValue>).Assembly.GetName().Name}/dropdown-list-control.js");
        }
        catch
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        CellHost?.AttachKeyRelay(null);
        if (_jsModule is null) return;
        try { await _jsModule.DisposeAsync(); }
        catch { /* circuit gone */ }
        _jsModule = null;
    }

    protected override void OnInitialized()
    {
        _gridEvents.RowSelected = EventCallback.Factory.Create<RowSelectEventArgs<TItem>>(
            this, args => OnGridRowSelected(args.Data));
        // Registered inside the mount render, before any key can arrive: keys the
        // user presses before this instance has focus reach it through the host.
        if (CellHost is { IsEditing: true })
            CellHost.AttachKeyRelay(HandleKeyDown);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Focus BEFORE measuring: focusing an in-cell editor can scroll its tree, and a
        // fixed popup measured first would be left at stale screen coordinates.
        if (EffectiveAutoFocus && !_autoFocused && firstRender)
        {
            _autoFocused = true;
            try { await FocusInputAsync(); } catch { /* disposed mid-focus */ }
        }

        // Only measure once the popup is actually in the DOM (it renders on the pass
        // AFTER _isOpen flips) — its real height decides the flip and the placement.
        var popupRendered = _isOpen;

        if (EffectiveOpenOnRender && !_openOnRenderInitialized)
        {
            _openOnRenderInitialized = true;
            if (!_isOpen && !Disabled && !ReadOnly)
            {
                SeedKeyboardIndex();
                ApplyHostInitialTextOnce();
                _isOpen = true;
                StateHasChanged();
            }
        }
        if (popupRendered && _isOpen && !_popupPlaced && !_popupFallback)
        {
            var module = await GetJsModuleAsync();
            if (module is null)
            {
                _popupFallback = true;
            }
            else
            {
                try
                {
                    // The helper measures the rendered popup itself (header, optional
                    // search bar and borders included); the large cap never binds.
                    var geometry = await module.InvokeAsync<PopupGeometry>(
                        "measureDropdown", _containerRef, 4000, 8, Px(PopupWidth, 450), _popupRef);
                    _popupTop = geometry.Top;
                    _popupLeft = geometry.Left;
                    _popupPlaced = true;
                }
                catch
                {
                    _popupFallback = true;
                }
            }
            StateHasChanged();
        }
        // The popup grid mounts a render after the open — apply the pending
        // keyboard highlight once it exists, then RETAKE focus: the reveal
        // scroll can land focus on the popup grid, which would route the next
        // arrow keys to the host tree instead of this combo.
        if (_pendingKeyboardHighlight && _isOpen && _gridRef != null)
        {
            _pendingKeyboardHighlight = false;
            await HighlightKeyboardIndexAsync();
            try { await FocusInputAsync(); } catch { /* disposed mid-focus */ }
        }
    }

    private string ComputedContainerStyle => $"width: {Width ?? "100%"}; position: relative;";
    private string ComputedPopupPlacement =>
        _popupPlaced
            ? FormattableString.Invariant($"position: fixed; top: {_popupTop:0.##}px; left: {_popupLeft:0.##}px; visibility: visible;")
            : _popupFallback
                ? "position: absolute; top: 100%; left: 0; visibility: visible;"
                : "position: fixed; top: -9999px; left: -9999px; visibility: hidden;";

    private string ComputedPopupStyle => $"width: {PopupWidth ?? "450px"}; {ComputedPopupPlacement} z-index: 10001; background: #ffffff !important; border: 1px solid #cbd5e1; border-radius: 6px; box-shadow: 0 10px 30px rgba(0,0,0,0.25), 0 4px 12px rgba(0,0,0,0.15);";

    private bool HasValue => Value != null && !string.IsNullOrEmpty(Value.ToString());

    private IEnumerable<TItem> FilteredDataSource
    {
        get
        {
            if (DataSource == null) return Enumerable.Empty<TItem>();
            if (string.IsNullOrWhiteSpace(_searchText)) return DataSource;
            var search = _searchText.Trim();
            var props = typeof(TItem).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            return DataSource.Where(item =>
            {
                if (item == null) return false;
                foreach (var prop in props)
                {
                    var val = prop.GetValue(item)?.ToString();
                    if (!string.IsNullOrEmpty(val) && val.Contains(search, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            });
        }
    }

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
        if (EffectiveOpenOnRender && !pressStartedHere) return;
        SeedKeyboardIndex();
        _isOpen = true;
        StateHasChanged();
    }

    /// <summary>Closes the popup (if open) and raises <see cref="Closed"/>.</summary>
    private async Task ClosePopupAsync()
    {
        if (!_isOpen) return;
        _isOpen = false;
        _searchText = "";
        _keyboardIndex = -1;
        _pendingKeyboardHighlight = false;
        _popupPlaced = false;
        _popupFallback = false;
        if (Closed.HasDelegate) await Closed.InvokeAsync();
        // The popup closing (a pick, Escape, a click away) ends a hosted cell edit.
        CellHost?.EndEdit();
        StateHasChanged();
    }

    // ── Keyboard navigation (owner directive 2026-09-02): Down opens the
    // list / moves the highlight, Up moves it back, Enter picks (or opens
    // when closed), Escape closes/dismisses. The highlight rides GridControl's
    // programmatic selection (SelectAndRevealRowAsync fires no RowSelected, so
    // arrowing never commits). Navigation order = DataSource order; the popup
    // search filter is not consulted (in-cell consumers run ShowSearch=false).
    private int _keyboardIndex = -1;
    private bool _pendingKeyboardHighlight;

    // Hosted cells only (vsFlexGrid ComboSearch): typed characters move the highlight
    // to the first row whose closed text starts with them — the typed start the host
    // hands over (InitialText) included. The buffer resets after a pause; the same
    // character repeated cycles through its matches.
    private string _typeSelectBuffer = "";
    private DateTime _typeSelectLastInputUtc = DateTime.MinValue;
    private bool _hostInitialTextApplied;
    private static readonly TimeSpan TypeSelectResetDelay = TimeSpan.FromSeconds(2.5);

    private List<TItem> KeyboardItems => FilteredDataSource.ToList();

    private async Task HandleKeyDown(KeyboardEventArgs e)
    {
        switch (e.Key)
        {
            case "Escape":
                if (_isOpen)
                    await ClosePopupAsync();
                else
                {
                    if (Closed.HasDelegate)
                        await Closed.InvokeAsync();   // dismiss a closed in-cell editor
                    CellHost?.EndEdit();
                }
                break;

            case "ArrowDown":
            case "Down":
                if (!_isOpen) OpenFromKeyboard();
                else await MoveKeyboardHighlightAsync(1);
                break;

            case "ArrowUp":
            case "Up":
                if (_isOpen) await MoveKeyboardHighlightAsync(-1);
                else if (CellHost is not null) await CellHost.KeyDownAsync(e);   // closed: the host moves rows
                break;

            case "ArrowLeft":
            case "Left":
            case "ArrowRight":
            case "Right":
                // Closed under a host: the host moves the cell cursor.
                if (!_isOpen && CellHost is not null && !e.AltKey && !e.CtrlKey && !e.MetaKey && !e.ShiftKey)
                    await CellHost.KeyDownAsync(e);
                break;

            case "Enter":
            case "NumpadEnter":
                if (!_isOpen)
                {
                    // vsFlexGrid edit mode: Enter on a CLOSED hosted editor finishes the
                    // edit (Down / F4 / Alt+Down drop the list); without a host it opens.
                    if (CellHost is not null) await CellHost.KeyDownAsync(e);
                    else OpenFromKeyboard();
                }
                else
                {
                    var items = KeyboardItems;
                    if (_keyboardIndex >= 0 && _keyboardIndex < items.Count)
                        await OnGridRowSelected(items[_keyboardIndex]);
                }
                break;

            default:
                if (CellHost is not null && !e.AltKey && !e.CtrlKey && !e.MetaKey
                    && e.Key is { Length: 1 } && !char.IsControl(e.Key[0]) && !char.IsWhiteSpace(e.Key[0]))
                    await TypeSelectAsync(e.Key);
                break;
        }
    }

    /// <summary>The input's focus step: under a cell host it goes through the host,
    /// which focuses only while it still owns the keyboard.</summary>
    private Task FocusInputAsync() => CellHost is not null ? CellHost.FocusAsync(_inputRef) : _inputRef.FocusAsync().AsTask();

    private void OpenFromKeyboard()
    {
        if (Disabled || ReadOnly) return;
        SeedKeyboardIndex();
        _isOpen = true;
    }

    /// <summary>A typed character on a hosted cell: opens the popup if needed and
    /// moves the highlight to the first row starting with the typed text.</summary>
    private async Task TypeSelectAsync(string ch)
    {
        if (Disabled || ReadOnly) return;
        var now = DateTime.UtcNow;
        var restart = now - _typeSelectLastInputUtc > TypeSelectResetDelay;
        _typeSelectLastInputUtc = now;
        if (!_isOpen) OpenFromKeyboard();   // seeds the highlight at the current value first
        var buffer = restart ? "" : _typeSelectBuffer;
        // Relayed before the mount render was acknowledged: the typed start that began
        // the edit has not been searched yet — search it first, exactly as the mount
        // does, then let this key extend it (never replace it).
        if (!_hostInitialTextApplied && CellHost is { InitialText: { Length: > 0 } })
        {
            ApplyHostInitialTextOnce();
            buffer = _typeSelectBuffer;
        }
        ApplyTypeSelect(buffer + ch);
        if (_isOpen && _gridRef != null && !_pendingKeyboardHighlight)
        {
            await HighlightKeyboardIndexAsync();
            try { await FocusInputAsync(); } catch { /* disposed mid-focus */ }
        }
        StateHasChanged();
    }

    /// <summary>The typed start that began a hosted edit searches like a typed key,
    /// once — whichever comes first, the mount's own step or a key relayed before it.</summary>
    private void ApplyHostInitialTextOnce()
    {
        if (_hostInitialTextApplied || CellHost is not { InitialText: { Length: > 0 } prefix }) return;
        _hostInitialTextApplied = true;
        _typeSelectLastInputUtc = DateTime.UtcNow;
        ApplyTypeSelect(prefix);
    }

    /// <summary>Moves the keyboard highlight to the first row whose closed text
    /// starts with <paramref name="prefix"/>; the same character repeated cycles
    /// through its matches from the current row; no match keeps the highlight.</summary>
    private void ApplyTypeSelect(string prefix)
    {
        _typeSelectBuffer = prefix;
        var items = KeyboardItems;
        if (items.Count == 0) return;
        var repeated = prefix.Length > 1 && prefix.All(c => char.ToUpperInvariant(c) == char.ToUpperInvariant(prefix[0]));
        var search = repeated ? prefix[..1] : prefix;
        var start = repeated && _keyboardIndex >= 0 ? _keyboardIndex + 1 : 0;
        for (var n = 0; n < items.Count; n++)
        {
            var i = (start + n) % items.Count;
            var text = GetItemFieldText(items[i], ClosedTextFieldName ?? TextFieldName);
            if (!string.IsNullOrEmpty(text) && text.StartsWith(search, StringComparison.OrdinalIgnoreCase))
            {
                _keyboardIndex = i;
                return;
            }
        }
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
        try { await FocusInputAsync(); } catch { /* disposed mid-focus */ }
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
        _keyboardIndex = 0;
        StateHasChanged();
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
