using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Fx.ControlKit.Grid;

/// <summary>Runtime state of the opt-in horizontal column window.</summary>
public enum GridColumnVirtualizationStatus
{
    Disabled,
    Active,
    Fallback
}

public partial class GridControl<TValue>
{
    /// <summary>
    /// Renders only the horizontally visible, non-frozen columns plus
    /// <see cref="ColumnVirtualizationOverscan"/> columns on either side.
    /// Frozen columns remain mounted. The feature is disabled by default and
    /// currently targets flat table layouts.
    /// </summary>
    [Parameter] public bool EnableColumnVirtualization { get; set; }

    /// <summary>Extra non-frozen columns retained before and after the viewport.</summary>
    [Parameter] public int ColumnVirtualizationOverscan { get; set; } = 2;

    /// <summary>
    /// Optional deterministic pixel width for columns that do not declare a
    /// pixel/unitless <see cref="GridColumn.Width"/> and do not have a runtime
    /// resize width. Leave at zero to make such a column select the safe full-
    /// render fallback.
    /// </summary>
    [Parameter] public double ColumnVirtualizationDefaultWidth { get; set; }

    /// <summary>
    /// Shows a compact explanation when virtualization was requested but the
    /// current grid shape cannot be windowed safely.
    /// </summary>
    [Parameter] public bool ShowColumnVirtualizationFallback { get; set; } = true;

    /// <summary>Reports whether the requested column window is active.</summary>
    public GridColumnVirtualizationStatus ColumnVirtualizationStatus
    {
        get
        {
            if (!EnableColumnVirtualization)
                return GridColumnVirtualizationStatus.Disabled;
            return TryCreateColumnVirtualizationLayout(out _, out _)
                ? GridColumnVirtualizationStatus.Active
                : GridColumnVirtualizationStatus.Fallback;
        }
    }

    /// <summary>
    /// Human-readable reason for a safe full-column render. Null when the
    /// feature is disabled or active.
    /// </summary>
    public string? ColumnVirtualizationFallbackReason
    {
        get
        {
            if (!EnableColumnVirtualization)
                return null;
            _ = TryCreateColumnVirtualizationLayout(out _, out var reason);
            return reason;
        }
    }

    private double _columnWindowScrollLeft;
    private double _columnWindowViewportWidth;
    private bool _columnWindowScrollRegistered;
    private string? _registeredColumnWindowSignature;
    private ColumnVirtualizationLayout? _columnVirtualizationRenderLayout;

    private string ColumnVirtualizationDomState => ColumnVirtualizationStatus switch
    {
        GridColumnVirtualizationStatus.Active => "active",
        GridColumnVirtualizationStatus.Fallback => "fallback",
        _ => "disabled"
    };

    private void BeginColumnVirtualizationRenderPass()
        => _columnVirtualizationRenderLayout = null;

    private ColumnVirtualizationLayout GetColumnVirtualizationRenderLayout()
    {
        if (_columnVirtualizationRenderLayout != null)
            return _columnVirtualizationRenderLayout;

        if (TryCreateColumnVirtualizationLayout(out var layout, out _))
            return _columnVirtualizationRenderLayout = layout;

        var columns = VisibleColumns.ToList();
        return _columnVirtualizationRenderLayout = ColumnVirtualizationLayout.Full(columns);
    }

    private bool IsColumnVirtualizationActive
        => GetColumnVirtualizationRenderLayout().IsVirtualized;

    private int RenderedColumnSlotCount
        => GetColumnVirtualizationRenderLayout().Slots.Count
            + (ShowCheckboxColumn ? 1 : 0)
            + (ShowDetailExpandColumn ? 1 : 0)
            + (ShowRowReorderColumn ? 1 : 0)
            + (ShowRowSelectorHandleColumn ? 1 : 0)
            + GroupedPlaceholderCount;

    private bool TryCreateColumnVirtualizationLayout(
        out ColumnVirtualizationLayout layout,
        out string? fallbackReason)
    {
        var columns = VisibleColumns.ToList();
        layout = ColumnVirtualizationLayout.Full(columns);
        fallbackReason = null;

        if (!EnableColumnVirtualization)
            return false;

        if (_pivotMode || (ShowAsChart && ChartValueFields is { Count: > 0 }))
        {
            fallbackReason = "pivot and chart views render their complete schema";
            return false;
        }

        if (AllowGrouping && _groupDescriptors.Count > 0)
        {
            fallbackReason = "grouped rows require the complete column set";
            return false;
        }

        if (_isEditing && EditSettingsRef?.Mode == EditMode.Inline)
        {
            fallbackReason = "inline row editing requires the complete column set";
            return false;
        }

        if (ColumnHeaderBands is { Count: > 0 }
            || columns.Any(column => !string.IsNullOrWhiteSpace(column.HeaderBand)))
        {
            fallbackReason = "multi-column header bands require the complete column set";
            return false;
        }

        if (RowTemplate != null)
        {
            fallbackReason = "a custom RowTemplate owns its complete cell layout";
            return false;
        }

        if (DataLayoutMode == GridDataLayoutMode.Stacked
            || AdaptiveMode != GridAdaptiveMode.None)
        {
            fallbackReason = "stacked/adaptive rows do not use a horizontal column window";
            return false;
        }

        if (columns.Count == 0 || columns.All(column => column.EffectiveIsFrozen))
        {
            fallbackReason = "there are no scrollable columns to virtualize";
            return false;
        }

        var widths = new double[columns.Count];
        for (var index = 0; index < columns.Count; index++)
        {
            if (!TryResolveColumnVirtualizationWidth(columns[index], out widths[index], out var widthReason))
            {
                var name = string.IsNullOrWhiteSpace(columns[index].DisplayHeader)
                    ? columns[index].Field
                    : columns[index].DisplayHeader;
                fallbackReason = $"column '{name}' {widthReason}";
                return false;
            }
        }

        var structuralWidth = 0d;
        if (ShowRowReorderColumn) structuralWidth += RowReorderColumnWidth;
        if (ShowRowSelectorHandleColumn) structuralWidth += ResolvedRowSelectorHandleWidth;
        if (ShowCheckboxColumn) structuralWidth += 50;
        if (ShowDetailExpandColumn) structuralWidth += 36;

        // First paint has no browser viewport measurement yet. Render the
        // complete schema for that one pass rather than guessing a width and
        // risking a white strip on a viewport wider than the guess. The
        // registration below immediately reports real geometry and converts
        // subsequent paints to the bounded window.
        if (_columnWindowViewportWidth <= 0)
        {
            layout = new ColumnVirtualizationLayout(
                columns,
                columns.Select((column, index) =>
                    ColumnRenderSlot.ForColumn(column, index, widths[index])).ToList(),
                widths,
                structuralWidth + widths.Sum(),
                true,
                BuildColumnVirtualizationSignature(columns, widths));
            return true;
        }

        var viewportWidth = _columnWindowViewportWidth;
        // Window against each column's normal-flow table coordinates. Sticky
        // columns may be pinned from anywhere in the order and only begin
        // covering the viewport after reaching their sticky edge, so eagerly
        // subtracting every frozen width can incorrectly omit an early column.
        // Frozen columns are retained separately; rendering a covered neighbor
        // is the conservative/correct trade-off.
        var visibleStart = Math.Max(0, _columnWindowScrollLeft);
        var visibleEnd = Math.Max(visibleStart + 1, _columnWindowScrollLeft + viewportWidth);

        var scrollableIndices = new List<int>(columns.Count);
        var columnStarts = new double[columns.Count];
        var cursor = structuralWidth;
        for (var index = 0; index < columns.Count; index++)
        {
            columnStarts[index] = cursor;
            if (!columns[index].EffectiveIsFrozen)
                scrollableIndices.Add(index);
            cursor += widths[index];
        }

        var firstScrollableOrdinal = -1;
        var lastScrollableOrdinal = -1;
        for (var ordinal = 0; ordinal < scrollableIndices.Count; ordinal++)
        {
            var index = scrollableIndices[ordinal];
            var start = columnStarts[index];
            var end = start + widths[index];
            if (end >= visibleStart && start <= visibleEnd)
            {
                if (firstScrollableOrdinal < 0)
                    firstScrollableOrdinal = ordinal;
                lastScrollableOrdinal = ordinal;
            }
        }

        // At an extreme edge, rounding can leave no intersection. Retain the
        // closest scrollable column so the DOM always has an anchor cell.
        if (firstScrollableOrdinal < 0)
        {
            var target = Math.Max(0, _columnWindowScrollLeft);
            var nearest = 0;
            var nearestDistance = double.MaxValue;
            for (var ordinal = 0; ordinal < scrollableIndices.Count; ordinal++)
            {
                var distance = Math.Abs(columnStarts[scrollableIndices[ordinal]] - target);
                if (distance < nearestDistance)
                {
                    nearest = ordinal;
                    nearestDistance = distance;
                }
            }
            firstScrollableOrdinal = lastScrollableOrdinal = nearest;
        }

        var overscan = Math.Max(0, ColumnVirtualizationOverscan);
        firstScrollableOrdinal = Math.Max(0, firstScrollableOrdinal - overscan);
        lastScrollableOrdinal = Math.Min(scrollableIndices.Count - 1, lastScrollableOrdinal + overscan);
        var retainedScrollable = scrollableIndices
            .Skip(firstScrollableOrdinal)
            .Take(lastScrollableOrdinal - firstScrollableOrdinal + 1)
            .ToHashSet();

        // Keyboard navigation and in-cell editing can move before a browser
        // scroll event arrives. Keep their logical target mounted so the
        // existing focus/scroll correction can reveal it; the subsequent
        // horizontal callback then recentres the ordinary window.
        if (_activeCell is { } activeCell
            && activeCell.CellIndex >= 0
            && activeCell.CellIndex < columns.Count
            && !columns[activeCell.CellIndex].EffectiveIsFrozen)
        {
            retainedScrollable.Add(activeCell.CellIndex);
        }
        if (!string.IsNullOrWhiteSpace(_batchEditField))
        {
            var batchEditIndex = columns.FindIndex(column =>
                string.Equals(column.Field, _batchEditField, StringComparison.OrdinalIgnoreCase));
            if (batchEditIndex >= 0 && !columns[batchEditIndex].EffectiveIsFrozen)
                retainedScrollable.Add(batchEditIndex);
        }
        if (AllowRowResizing && columns.Count > 0 && !columns[^1].EffectiveIsFrozen)
            retainedScrollable.Add(columns.Count - 1);

        var slots = new List<ColumnRenderSlot>();
        var omittedWidth = 0d;
        for (var index = 0; index < columns.Count; index++)
        {
            var retained = columns[index].EffectiveIsFrozen || retainedScrollable.Contains(index);
            if (!retained)
            {
                omittedWidth += widths[index];
                continue;
            }

            if (omittedWidth > 0)
            {
                slots.Add(ColumnRenderSlot.Spacer(omittedWidth));
                omittedWidth = 0;
            }
            slots.Add(ColumnRenderSlot.ForColumn(columns[index], index, widths[index]));
        }
        if (omittedWidth > 0)
            slots.Add(ColumnRenderSlot.Spacer(omittedWidth));

        layout = new ColumnVirtualizationLayout(
            columns,
            slots,
            widths,
            structuralWidth + widths.Sum(),
            true,
            BuildColumnVirtualizationSignature(columns, widths));
        return true;
    }

    private bool TryResolveColumnVirtualizationWidth(
        GridColumn column,
        out double width,
        out string reason)
    {
        if (column.RuntimeWidth.HasValue)
        {
            width = column.RuntimeWidth.Value;
        }
        else if (!string.IsNullOrWhiteSpace(column.Width))
        {
            var declared = TryParseWidthPx(column.Width);
            if (!declared.HasValue)
            {
                width = 0;
                reason = "uses a non-pixel Width";
                return false;
            }
            width = declared.Value;
        }
        else
        {
            width = ColumnVirtualizationDefaultWidth > 0
                ? ColumnVirtualizationDefaultWidth
                : 0;
        }

        if (!double.IsFinite(width) || width <= 0)
        {
            reason = "does not have a deterministic pixel width";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(column.MaxWidth))
        {
            var max = TryParseWidthPx(column.MaxWidth);
            if (!max.HasValue)
            {
                reason = "uses a non-pixel MaxWidth";
                return false;
            }
            width = Math.Min(width, max.Value);
        }

        // CSS sizing gives min-width precedence when min exceeds max, so apply
        // the floor last to mirror the browser's final used width.
        if (!string.IsNullOrWhiteSpace(column.MinWidth))
        {
            var min = TryParseWidthPx(column.MinWidth);
            if (!min.HasValue)
            {
                reason = "uses a non-pixel MinWidth";
                return false;
            }
            width = Math.Max(width, min.Value);
        }

        if (!double.IsFinite(width) || width <= 0)
        {
            reason = "resolves to a non-positive pixel width";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private string BuildColumnVirtualizationSignature(
        IReadOnlyList<GridColumn> columns,
        IReadOnlyList<double> widths)
        => string.Join('|', columns.Select((column, index) => string.Create(
            CultureInfo.InvariantCulture,
            $"{column.Field}:{widths[index]:0.##}:{column.EffectiveIsFrozen}:{column.EffectiveFrozenPosition}")));

    private string ColumnVirtualizationSpacerStyle(double width)
    {
        var px = width.ToString("0.##", CultureInfo.InvariantCulture);
        return $"width:{px}px;min-width:{px}px;max-width:{px}px;padding:0;overflow:hidden;"
            + "border-left:0;border-right:0;background:inherit;pointer-events:none;";
    }

    private string ColumnVirtualizationColumnWidthStyle(double width)
    {
        var px = width.ToString("0.##", CultureInfo.InvariantCulture);
        return $"width:{px}px;min-width:{px}px;max-width:{px}px;";
    }

    private int GetColumnAriaIndex(int columnIndex)
        => columnIndex + 1
            + (ShowRowReorderColumn ? 1 : 0)
            + (ShowRowSelectorHandleColumn ? 1 : 0)
            + (ShowCheckboxColumn ? 1 : 0)
            + (ShowDetailExpandColumn ? 1 : 0)
            + GroupedPlaceholderCount;

    private double GetColumnVirtualizedTableWidthPx()
        => GetColumnVirtualizationRenderLayout().TotalWidthPx;

    [JSInvokable]
    public async Task OnGridColumnWindowScrollAsync(double scrollLeft, double clientWidth)
    {
        if (!EnableColumnVirtualization)
            return;

        var before = GetColumnVirtualizationWindowKey();
        _columnWindowScrollLeft = Math.Max(0, scrollLeft);
        _columnWindowViewportWidth = Math.Max(0, clientWidth);
        _columnVirtualizationRenderLayout = null;
        var after = GetColumnVirtualizationWindowKey();
        if (!string.Equals(before, after, StringComparison.Ordinal))
            await InvokeAsync(StateHasChanged);
    }

    private string GetColumnVirtualizationWindowKey()
    {
        if (!TryCreateColumnVirtualizationLayout(out var layout, out _))
            return "fallback";
        return string.Join(',', layout.Slots.Select(slot => slot.IsSpacer
            ? $"s:{slot.WidthPx:0.##}"
            : $"c:{slot.ColumnIndex}"));
    }

    private async Task EnsureGridColumnWindowRegisteredAsync()
    {
        if (_gridJsModule == null)
            return;

        var active = TryCreateColumnVirtualizationLayout(out var layout, out _);
        if (!active)
        {
            if (_columnWindowScrollRegistered)
            {
                await _gridJsModule.InvokeVoidAsync("unregisterGridColumnWindow", _scrollElement);
                _columnWindowScrollRegistered = false;
                _registeredColumnWindowSignature = null;
            }
            return;
        }

        if (_columnWindowScrollRegistered
            && string.Equals(_registeredColumnWindowSignature, layout.Signature, StringComparison.Ordinal))
            return;

        _windowSelfRef ??= DotNetObjectReference.Create(this);
        await _gridJsModule.InvokeVoidAsync(
            "registerGridColumnWindow",
            _scrollElement,
            _windowSelfRef);
        _columnWindowScrollRegistered = true;
        _registeredColumnWindowSignature = layout.Signature;
    }

    private async Task DisposeGridColumnWindowAsync()
    {
        if (!_columnWindowScrollRegistered || _gridJsModule == null)
            return;
        await _gridJsModule.InvokeVoidAsync("unregisterGridColumnWindow", _scrollElement);
        _columnWindowScrollRegistered = false;
        _registeredColumnWindowSignature = null;
    }

    private sealed class ColumnVirtualizationLayout
    {
        public ColumnVirtualizationLayout(
            IReadOnlyList<GridColumn> columns,
            IReadOnlyList<ColumnRenderSlot> slots,
            IReadOnlyList<double> widths,
            double totalWidthPx,
            bool isVirtualized,
            string signature)
        {
            Columns = columns;
            Slots = slots;
            Widths = widths;
            TotalWidthPx = totalWidthPx;
            IsVirtualized = isVirtualized;
            Signature = signature;
        }

        public IReadOnlyList<GridColumn> Columns { get; }
        public IReadOnlyList<ColumnRenderSlot> Slots { get; }
        public IReadOnlyList<double> Widths { get; }
        public double TotalWidthPx { get; }
        public bool IsVirtualized { get; }
        public string Signature { get; }

        public static ColumnVirtualizationLayout Full(IReadOnlyList<GridColumn> columns)
            => new(
                columns,
                columns.Select((column, index) => ColumnRenderSlot.ForColumn(column, index, 0)).ToList(),
                Array.Empty<double>(),
                0,
                false,
                "full");
    }

    private sealed record ColumnRenderSlot(
        GridColumn? Column,
        int ColumnIndex,
        double WidthPx,
        bool IsSpacer)
    {
        public static ColumnRenderSlot ForColumn(GridColumn column, int index, double width)
            => new(column, index, width, false);

        public static ColumnRenderSlot Spacer(double width)
            => new(null, -1, width, true);
    }
}
