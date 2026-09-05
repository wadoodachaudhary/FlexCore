# FlexCore

General-purpose, application-agnostic Blazor controls for .NET — grids, tree-grids, charts, diagrams, reports, dialogs, ribbons, toolbars, notifications, multi-select dual-list, editors, and more.

```csharp
using Fx.ControlKit.Grid;
using Fx.ControlKit.Notifications;
```

## Highlights

- **GridControl** — virtualized data grid with sorting, filtering, grouping, drag-reorder, header context menu, dialog / batch / inline editing, metadata-generated columns, banded headers, cumulative left/right frozen columns, choose-columns dialog, and aggregate footers
- **TreeGridControl** — hierarchical version of the grid
- **ReportWriterControl** — adaptive paginated report renderer fed by a small `ReportDefinition` (loaded from Crystal XML, plain SQL, or any other source you wire)
- **DialogControl, NotificationService, RibbonControl, ToolbarControl, ButtonControl, DropDownListControl, MultiSelectDualListControl, ChartControl, DiagramControl, EditorPanelControl, OutlineControl, ProgressBarControl, PropertyGridControl, TabsControl** — the rest of the kit
- **App-agnostic by design** — every external dependency (DB, session, picklist source, report exporter) is exposed as an interface; host apps wire their own adapters in `Program.cs`

## Targets

- `.NET 10.0`
- Blazor Server or Blazor WebAssembly

## Install

Once published to NuGet:

```bash
dotnet add package FlexCore
```

Or reference the project directly:

```xml
<ProjectReference Include="..\path\to\FlexCore\FlexCore.csproj" />
```

## Quick start — a grid

```razor
@using Fx.ControlKit.Grid

<GridControl TValue="MyRow" DataSource="@rows"
             AllowSelection="true" AllowSorting="true"
             AllowFiltering="true" AllowGrouping="true">
    <GridColumnsBase>
        <GridColumn Field="Id"       HeaderText="ID"       Width="80px" />
        <GridColumn Field="Name"     HeaderText="Name"     Width="200px" />
        <GridColumn Field="Quantity" HeaderText="Qty"      Width="100px"
                    Type="ColumnType.Number" Format="N0" TextAlign="TextAlign.Right" />
    </GridColumnsBase>
</GridControl>

@code {
    record MyRow(int Id, string Name, int Quantity);
    List<MyRow> rows = new() { new(1,"Foo",10), new(2,"Bar",20) };
}
```

Columns can alternatively be generated from model metadata. `Display`,
`DisplayName`, `Editable`, `DisplayFormat`, `Key`, and `ScaffoldColumn` are
honored; the feature is opt-in so existing dynamic layouts are unaffected.

```razor
<GridControl TValue="MyAnnotatedRow" DataSource="@rows"
             AutoGenerateColumns="true" />
```

For explicit columns, set the same `HeaderBand` on adjacent columns, or pass
`ColumnHeaderBands` with `GridColumnHeaderBand` descriptors when a band needs
a custom template, CSS class, alignment, or field membership.

### Horizontal column virtualization

Wide flat grids can opt into a real horizontal column window. FlexCore keeps
left/right frozen columns mounted, renders the visible non-frozen columns plus
overscan, and substitutes fixed-width table spacers for omitted runs. Existing
grids are unchanged because the feature defaults off.

```razor
<GridControl TValue="WideRow" DataSource="@rows" Height="420px"
             WidthMode="GridWidthMode.FitColumns"
             EnableColumnVirtualization="true"
             ColumnVirtualizationOverscan="2">
    <GridColumnsBase>
        <GridColumn Field="Id" Width="72px" IsFrozen="true" />
        <GridColumn Field="Metric01" Width="120px" />
        @* ...more fixed-width columns... *@
        <GridColumn Field="Status" Width="100px" IsFrozen="true"
                    FrozenPosition="FrozenColumnPosition.Right" />
    </GridColumnsBase>
</GridControl>
```

Every participating column must resolve to a deterministic pixel width from
`RuntimeWidth`, a pixel/unitless `Width`, or the optional
`ColumnVirtualizationDefaultWidth`. Percentage/content widths, active grouping,
header bands, stacked rows, custom row templates, and inline row editing use a
safe full-column render. Inspect `ColumnVirtualizationStatus` and
`ColumnVirtualizationFallbackReason`; the compact fallback label can be hidden
with `ShowColumnVirtualizationFallback="false"`.

### Custom multi-column sorting

With `AllowSorting="true"`, the standard right-click header context menu includes
**Multi-sort → Custom Sort...**. `AllowMultiSorting` defaults to `true`; set it to
`false` to restrict a grid to one sort column. The section appears alongside the
grouping/expand/collapse commands when grouping is enabled and does not require
`ShowColumnMenu`. Hosts can also open the same FlexCore dialog with
`await grid.OpenSortDialogAsync()`.

Three-dot header buttons are off by default, including when `ShowColumnMenu` is
on. Set `ShowColumnMenuButton="true"` only when that shortcut is wanted.

The editor starts with the current sort levels. Add/delete levels, choose each
column and order, and move levels up/down to set priority. Apply commits the
whole list; Cancel, Escape, and the close button discard edits. Clear All followed
by Apply removes sorting. Only sortable columns are offered, with no duplicates.
The existing Sorting callbacks can veto the whole change. Local rows, header
priority indicators, state snapshots, exports, and one provider reload all use
the same ordered descriptors.

### Typed filtering and validation

`GridFilterMode.Simple`, `SimpleWithMenu`, and `Excel` render type-aware filter
editors using FlexCore dropdowns and textboxes. By default, every column has an
operator selector; number/date comparisons use typed values and Boolean columns
use a True/False selector. Set `FilterSettings.ShowFilterRowOperators = false`
for a textbox-only row: every column uses Contains against raw/display text,
including numbers, dates, and Booleans. Clearing a textbox removes its filter. The menu supports
two conditions joined by AND/OR; Excel mode adds a searchable checklist.
Search, filter-row typing, and auto-applied menu typing share
`ImmediateModeDelay`. Searching an Excel checklist selects only the matching
values; Auto Apply commits that selection after the delay, while manual mode
stages it until Apply. Checkboxes refine those matches, and clearing the search
selects all values again. The typed conditions remain independent.

```razor
<GridControl TValue="MyRow" DataSource="@rows"
             FilterMode="GridFilterMode.SimpleWithMenu"
             FilterSettingsRef="@filterSettings" />

@code {
    FilterSettings filterSettings = new()
    {
        ImmediateModeDelay = 250,
        EnableCaseSensitivity = false
    };
}
```

Columns can replace the standard surfaces with `FilterCellTemplate`,
`FilterMenuTemplate`, or `FilterMenuButtonsTemplate`. Their context objects
expose staged values/operators and explicit Apply/Clear callbacks.

For a local `DataSource`, checklist candidates are calculated from the local
query. An `ItemsProvider` does not infer the complete value universe from its
loaded row window. Provider checklists therefore require the opt-in
`EnableProviderFilterValueRequests` contract described below.

Generated Inline/Dialog editors use FlexCore `TextBoxControl`, `CheckBoxControl`,
and `DatePickerControl`; dialog chrome and actions use `DialogControl` and
`ButtonControl`. Dates use the FlexCore calendar rather than a native date input.
Generated text editors keep their latest typed draft across parent renders,
including invalid numeric/text values awaiting validation. Enter saves a row
draft; Escape cancels. Caller-supplied `EditTemplate` content remains caller-owned.

For Batch mode, typing with several rows selected stages a shared value. Enter,
Tab/navigation, clicking another cell, or `EndEditAsync()` commits it through the
same validation and cell-save path as an in-cell editor. If `OnTypeAheadCommit`
is supplied, that handler owns applying the bulk value. Without it, the grid
updates eligible selected rows itself. Invalid typed values retain the editor
and leave all targets unchanged.

To show checkboxes while allowing plain clicks to replace a selection, use
`SelectionSettings.ShowCheckboxes = true`, `CheckboxOnly = false`, and
`MultiSelectBehavior = GridMultiSelectBehavior.VBMultiSelect`. Ctrl/Cmd, Shift,
and drag still build multiple selections. `CheckboxOnly = true` continues to
reserve selection changes for checkboxes. The selection-checkbox cell and
read-only/primary-key columns are keyboard navigable. Arrows and Tab move the
active border through checkbox, ID, and data cells; Space/Enter toggles an active
selection checkbox. Navigation does not make primary keys editable.

Model validation is opt-in when `ValidationSettings` is non-null. Type conversion
is always checked before a batch change is applied. When enabled, Inline and Dialog editing use a
real Blazor `EditContext`; DataAnnotations, `IValidatableObject`, a custom
callback, and an optional validator component are supported. Batch mode
performs property-level DataAnnotations/custom validation before committing.

```csharp
GridValidationSettings<MyRow> validation = new()
{
    EnableDataAnnotations = true,
    ShowFieldMessages = true,
    ShowValidationSummary = true,
    CustomValidator = request => Array.Empty<ValidationResult>()
};
```

### Enterprise layout, templates, and state

Local multi-sort applies every active descriptor in visible priority order:
the first descriptor establishes `OrderBy`, and later descriptors refine ties
with `ThenBy`. The same ordered descriptor list is emitted to an
`ItemsProvider`; the provider remains responsible for translating every
descriptor into its database query.

`ShowColumnMenu="true"` adds sort, filter, clear, hide/show, best-fit, and
left/right lock commands to the header context menu. Locks use cumulative sticky offsets
and are included in `GridSettings`; existing right-click behavior is unchanged
when the option is off. `AutoGenerateColumns`, one-level `ColumnHeaderBands`,
and `GridColumn.HeaderBand` cover metadata-driven and banded schemas.

For a local `DataSource`, pager placement can be `Top`, `Bottom`, or `Both`.
`ItemsProvider` mode is range-window virtualization and does not turn those
requests into a remote page-number UI. `PagerTemplate`,
`GridToolBarTemplate`, `RowTemplate`, `DetailTemplate`, `NoDataTemplate`,
`FilterCellTemplate`, `FilterMenuTemplate`, `FilterMenuButtonsTemplate`, and
`ColumnChooserTemplate` expose the main grid surfaces without requiring
hand-authored cell artifacts for ordinary columns.

`GetState()` / `GetStateAsync()` and `SetStateAsync()` round-trip a versioned,
JSON-serializable `GridState`: column layout, ordered multi-sort, every filter
surface, search/expression text, page/page size, selected rows/cells, active
cell, expanded details, and collapsed groups. This is a complete round-trip for
local data when `ItemKeySelector` (or a stable implicit key) identifies rows.
In provider mode, query/layout/group state is restored and the first range or
lazy group tree is reloaded; selected-cell, active-cell, and expanded-detail
keys are restored only if their rows are in that loaded provider data. The grid
does not issue arbitrary key lookups for unloaded rows. `OnStateChanged` and
the CLR `StateChanged` event publish detached snapshots.

```razor
<GridControl TValue="Order" DataSource="@orders"
             ShowColumnMenu="true"
             PagerPosition="GridPagerPosition.Both"
             AdaptiveMode="GridAdaptiveMode.Auto"
             AccessibleLabel="Orders"
             OnStateChanged="SaveStateAsync" />
```

`GridDataLayoutMode.Stacked` explicitly renders field cards, while
`GridAdaptiveMode.Auto` switches to that layout below the built-in narrow-screen
breakpoint. The focusable table exposes grid/row/gridcell roles, an accessible
name, column metadata, selection, expansion and sort state; active generated
cells are connected with `aria-activedescendant`. Exact flat grids publish
header-aware row counts and indexes. Dynamic group/detail/edit/footer structures
publish `aria-rowcount="-1"` instead of an incorrect record-only count and follow
DOM order. A host-supplied `RowTemplate` owns the semantics of the cells it
renders. `CustomKeyboardShortcuts` maps normalized gestures such as `Ctrl+Home`
to `GridKeyboardCommand` values.

Local exports already support real XLSX (via ClosedXML), CSV/TSV, HTML/XLS,
JSON, and PDF. They preserve visible column order, widths, formatting, and
aggregate output. The provider APIs below extend this to the complete remote
query instead of only the loaded window.

### Provider grouping and complete-query export

`ItemsProvider` remains a flat range API by default. Large remote datasets can
opt into lazy grouping with `EnableProviderGrouping="true"`. The same delegate
then receives a `Purpose` of `Groups`, `GroupChildren`, or `GroupItems`, plus a
typed `GroupRequest` ancestry. It returns provider-computed group counts and
aggregates through `GridProviderGroup<T>` and `GridAggregateResult`. Root and
child pages expose `ResultSetCount`/`HasMore`, so groups and large leaf groups
can load incrementally as the user expands them.

```razor
<GridControl TValue="Order" ItemsProvider="LoadOrdersAsync"
             Height="520px" AllowGrouping="true"
             GroupColumns="@(new() { nameof(Order.Region), nameof(Order.Category) })"
             EnableProviderGrouping="true"
             ProviderGroupsInitiallyCollapsed="true"
             ProviderGroupPageSize="100"
             ProviderGroupItemPageSize="250" />
```

This API is additive: the original `GridItemsRequest` and
`GridItemsResult<T>` constructors are unchanged, `Purpose` defaults to `Rows`,
and provider grouping defaults off.

Provider filtering, sorting, search, and grouping are descriptor contracts,
not an in-memory second pass: the provider must translate `request.Query` into
its own LINQ, SQL, or other backend query before applying `StartIndex` and
`Count`. The enterprise bench deliberately records each request so descriptor
handling is visible.

To populate an Excel-style checklist from remote data, enable
`EnableProviderFilterValueRequests`. Opening a filter menu then sends
`Purpose=FilterValues` with `FilterField`; return distinct values in
`GridItemsResult.FilterValues`, and use `ResultSetCount`/`HasMore` to report
whether the set is complete. The grid requests at most
`ProviderFilterValueRequestSize` values in one call. It will not apply a
partial checklist inclusion filter while the request is loading, failed, has
`HasMore=true`, or otherwise has unconfirmed completeness; narrow the query or
increase the cap first. Remote checklist search/load-more and provider-built
numeric histogram buckets are not separate protocols today.

Flat `Rows` responses may also return `Aggregates`; when present, the grid
uses those provider-computed values for footer totals instead of calculating
against only the currently painted range.

Async exports also preserve the original loaded-window behavior by default.
Set `ExportAllProviderItems="true"` to make the normal `ExportAsync` helpers
page the full current remote query, or call `ExportProviderQueryAsync`
explicitly. Export requests use `Purpose=Export`; they do not replace the
visible provider window or its cache. `ProviderExportProgressChanged`,
`ProviderExportProgress`, and `CancelProviderExport()` support progress and
cancellation. `CreateProviderExportAsync` returns bytes without opening a save
dialog. A provider must advance each requested range; an empty page before its
reported total is reached fails safely instead of producing a silently
truncated export. The export currently accumulates the complete result in
memory before building the output, so hosts should choose
`ProviderExportPageSize` and server limits appropriate to their largest
permitted export; it is not a streaming XLSX/CSV writer.

See the Showcase route `/demo/grid/provider-enterprise` for a provider that
implements two grouping levels, lazy leaf-row paging, aggregates, full-query
export, progress, and cancellation.

### Delimited-text encoding and XLSX formulas

CSV and TSV keep their historical UTF-8-without-BOM output by default. Set a
grid's `DelimitedExportEncoding`, or use an overload such as
`ExportToCsvWithEncodingAsync(GridDelimitedTextEncoding.Utf8WithBom, "items.csv")`, to
select UTF-8 with BOM, UTF-16 little-endian with BOM, or ASCII. The same option
is available through `ExportWithEncodingAsync`, `CreateExportWithEncoding`,
`GridExporter.ExportWithEncoding`, and the complete-provider
`...WithEncodingAsync` methods. These names intentionally avoid ambiguous calls
with the existing file-name overloads. ASCII replaces characters outside its
character set with `?`; encoding choices are ignored by non-delimited formats.

For XLSX, a `GridColumn.Formula` using FlexCore's arithmetic formula syntax is
written as a real same-row A1 formula when every referenced field maps uniquely
to an exported, non-formula column. Both `[Unit Price] * [Qty]` and
`UnitPrice * Qty` forms are supported. Function calls, ranges, string literals,
references to hidden or duplicate fields, formula-to-formula references, and
self references deliberately fall back to the existing evaluated cell value.
XLSX keeps native number, Boolean, date and time values and translates common
.NET number/date formats (such as `C2`, `N2`, and `d`) into Excel number-format
codes; formulas therefore reference numeric operands rather than formatted text.
CSV, TSV, HTML, XLS, JSON, and PDF keep the established display/evaluated value.

## Verified parity fixes in existing controls

See [the audit follow-up](docs/parity-audit-follow-up.md) for verified findings,
existing implementations, and remaining limits. No new production UI components
were introduced for this work.

`BarcodeControl.Type` selects a real symbology (default `Code128`), and
`QrCodeControl.ErrorCorrection` selects QR correction strength. Both controls
render encoded SVG, clear empty values, and show an error for invalid payloads.

`PivotControl.SortDescriptors` / `SortDescriptorsChanged` support saved or bound
sorting. `SortByAsync(field, direction, add: true)` adds a dimension or rendered
value-column sort; null removes it. Header buttons cycle ascending, descending,
and clear; Shift adds a level. Row and column field chips sort their dimensions
in interactive mode. Nested dimensions stay together; value sorts order sibling
rows by their aggregates.

`DataFilterControl` uses field types for operators and existing FlexCore editors.
Its `Value` can be replaced or reset to null after first render. A field's
`ValueTemplate` receives `Condition`, `Property`, and `SetValueAsync`. For local
data, rebuild a predicate when the filter changes:

```csharp
try
{
    var predicate = FilterEvaluator.BuildPredicate<Order>(filterGroup, properties,
        CultureInfo.CurrentCulture, caseSensitive: false);
    visibleOrders = allOrders.Where(predicate).ToList();
}
catch (FormatException error)
{
    filterError = error.Message;
}
```

This API validates and snapshots the filter before enumeration. Empty drafts
are ignored. `BuildExpression` and `OnExpressionChanged` provide display text,
not SQL or an executable query language; backend translation remains the host's
responsibility.

`EditSettings.ShowConfirmDialog` now controls row-command and toolbar deletion.
The selected batch gets one confirmation, with Cancel initially focused.
`RowDeleting` may still veto individual records. F2 opens an editable cell or
selected inline/dialog row. Existing `DeleteRecordAsync` remains a direct
programmatic operation.

`DialogControl` now labels its dialog role from `Header`; `AriaLabel` can override
that label, and `AriaDescribedBy` can refer to a short caller-supplied description.

Run [the component regression checks](tests/FlexCore.RegressionTests/README.md)
with `dotnet run --project tests/FlexCore.RegressionTests/FlexCore.RegressionTests.csproj`.

## License

MIT — see [LICENSE](LICENSE).
