# TreeView and TreeGrid: Syncfusion parity progress

Implemented 6 September 2026 against the [baseline Syncfusion audit](syncfusion-parity-audit.md). These changes extend the existing FlexCore controls. They do **not** establish full TreeGrid parity.

The reference behaviors are Syncfusion's [TreeView selection](https://blazor.syncfusion.com/documentation/treeview/node-selection), [checkbox hierarchy](https://blazor.syncfusion.com/documentation/treeview/check-box), [node editing](https://blazor.syncfusion.com/documentation/treeview/node-editing), [drag/drop](https://blazor.syncfusion.com/documentation/treeview/drag-and-drop), and [TreeGrid hierarchy filtering](https://blazor.syncfusion.com/documentation/treegrid/filter). FlexCore's API remains its own; this is functional convergence, not a source-compatible implementation of Syncfusion's API.

## Independent FlexKitTester benches

- `/flexcore-controls/tree-view` — bound multi-selection, cascade/mixed checks, lazy failure/retry, F2 rename and validation, add/remove/move, native drag/drop, templates, filtering, state restoration, and a 10,000-node virtualized sample.
- `/flexcore-controls/tree-grid` — header context-menu custom sorting, typed two-condition filters, hierarchy inclusion modes, paging, lazy loading, JSON view state, and CSV/XLSX/PDF downloads.

- `/flexcore-controls/tree-grid-operations` — inline/dialog/batch editing, validation and cancellation, add/delete subtrees, bound hierarchy checks, lazy check inheritance, guarded reparenting, native positional drag/drop, and left/right frozen columns.

All three are linked from `/flexcore-controls` (37 benches). Interactive fields, menus and dialogs use FlexCore controls. No new JavaScript module was added. TreeView uses the existing browser-capabilities bridge for browser key defaults, native drag payloads, and scoped scrolling; hierarchy, filtering, selection, loading and editing stay in C#.

## TreeView changes

| Audit | Implemented behavior |
| --- | --- |
| T02 | `TreeViewData.FromFlat` maps arbitrary self-referencing records to nodes, retains the original record in `Tag`, accepts children before parents, and rejects duplicate IDs and cycles. `HasUnloadedChildren` gives an unloaded branch an expander. `LoadChildren` supplies cancellable async children, with busy state, visible errors, retry, result validation, and caching. |
| T03 | `SelectedNodeIds`/`SelectedNodeIdsChanged`, `SelectNodesAsync`, Ctrl/Cmd toggling, Shift range selection, plain-click replacement, and Ctrl/Cmd focus-only keyboard movement. Disabled nodes are skipped. The ID collection takes precedence over the legacy primary-node parameter when both are supplied. |
| T04 | `CheckedNodeIds`/change event, `SetCheckedAsync`, `CheckAllAsync`, and optional `AutoCheck` cascade. Enabled ancestors derive checked/mixed state from enabled children; disabled branches are not cascaded into. Checked lazy parents pass their intent to loaded children. `AutoCheck` defaults to false for compatibility. |
| T05 | `AllowEditing`, F2/double-click/API rename, `NodeEditing`, cancellable `NodeEditCommitting`, post-commit `NodeEdited`, required-name validation, Enter/Save commit, and Escape/Cancel. Failed validation retains the editable draft. |
| T06 | `AllowDragAndDrop`, native same-tree drop into a loaded node, and `MoveNodeAsync` with Before/Inside/After positions. `CanDrop` and cancellable `NodeMoving` govern permission; `NodeMoved` reports success. Moves reject self/descendant cycles and disabled targets. |
| T07 | `NodeTemplate`, stable sibling ordering through `NodeComparer`, and text filtering that reveals matches and their ancestors without destroying stored expansion state. |
| T08 | Opt-in Blazor row virtualization, fixed `ItemSize`, bounded rendered DOM, and keyboard Home/End scrolling. Data and the flattened hierarchy remain in memory. |
| T09 | Bound expanded IDs, expand/collapse all, ensure-visible, add/remove subtree APIs and model/state callbacks. State can be saved and restored through the ID collections. |
| T10–T11 | Tree/treeitem roles, active descendant, levels and sibling counts, selected/expanded/disabled/busy state and mixed checks. Labels render as encoded text; rich content is explicitly provided through a template. |

Existing VB-style appearance options, icons and legacy activation callbacks remain. The default is still single selection, individual checks, nonvirtualized rendering, and no edit/drag interaction unless enabled.

### TreeView API notes and remaining limitations

- IDs must be nonempty and unique; hierarchy depth is limited to 512 levels. Unknown flat parent IDs become roots.
- Filtering and ID-based operations work on loaded nodes. The provider is a child loader, not a server query/search adapter. `EnsureVisibleAsync` requires a known loaded ID.
- `ExpandAllAsync()` expands loaded branches. Passing `loadChildren: true` loads the branches known at the start of that call; it does not crawl an unbounded remote hierarchy.
- Load an unloaded destination before adding or moving children into it. Replacing the source while a request is pending discards the stale result.
- Node rename is not a general form/CRUD engine. Hosts persist changes using the supplied callbacks and `Tag`.
- Native drop UI currently supports Inside; Before/After are exposed by the API and demonstrated in the bench. Cross-tree dragging, touch drag handles, hover expansion, and positional drop indicators remain gaps.
- Virtualization assumes fixed row height. Variable-height templates and a remote virtual item provider are not implemented.
- ARIA markup and keyboard behavior were exercised in browsers; this is not a completed screen-reader certification.

## TreeGrid changes

| Audit | Implemented behavior |
| --- | --- |
| R01–R02 | Validated flat ID/parent hierarchy and direct-child async provider using `TreatAsParent` for unloaded branches. Errors remain visible and retryable, successful results are cached, invalid child mappings are rejected, and stale source responses are discarded. |
| R03 | Opt-in paging of the visible flattened query with `PageSize`, `CurrentPage`, `PageCount`, `GoToPageAsync` and `PageChanged`. Keyboard selection changes pages to reveal the current row. |
| R04 | Stable ordered multi-sort through the shared `GridLocalSortPipeline`. Shift-click adds/removes header sort levels. The standard header context menu includes hierarchy commands and Custom sort; the FlexCore dialog supports adding, removing, ordering and applying levels. No default three-dot header button was added. |
| R05 | `SetFilterAsync(TreeGridFilter)` and FlexCore menus with typed numeric/date/Boolean comparisons, text operators, blanks, two conditions joined by AND/OR, cross-column AND, and optional case matching. `FilterHierarchyMode` supports None/Parent/Child/Both. A matching descendant is visible even when its ancestors were collapsed. Clearing filters restores expansion-based visibility. |
| R10 | `Export` and `DownloadAsync` reuse `GridExporter` for CSV, TSV, HTML, XLS, XLSX, PDF and JSON. The bench exercises CSV/XLSX/PDF. Exports include a Level column and all rows in the current visible query across pages; XLSX keeps native numeric/date values. `allLoaded: true` exports all currently loaded records. |
| R11 | `CaptureState`/`RestoreStateAsync` with a JSON-serializable `TreeGridViewState`: ordered sorts, filter conditions, hierarchy/case policy, expanded IDs, primary selection, page/page size, visibility and widths. Rendered rows have hierarchy ARIA attributes. |

### TreeGrid editing, checks, reparenting and frozen columns

The second increment implements the four requested feature groups, using the existing controls. References: Syncfusion [batch editing](https://blazor.syncfusion.com/documentation/treegrid/editing/batch-editing), [hierarchy checkbox columns](https://blazor.syncfusion.com/documentation/treegrid/column), and [frozen columns](https://blazor.syncfusion.com/documentation/treegrid/scrolling).

| Audit | Implemented behavior |
| --- | --- |
| R06 | `EditSettingsRef` enables Inline, Dialog or Batch mode, add/edit/delete permissions, F2/double-click/Enter entry, and optional second-click editing. `FormItemControl` supplies the existing FlexCore text, numeric-text, checkbox, enum, date and time editors. Each edited row has an independent cloned draft and `EditContext`, DataAnnotations validation, a custom `Validator`, and optional `EditValidatorTemplate` / column `EditTemplate`. Invalid typed text stays in the editor. Enter commits inline/dialog rows and stages batch rows; Escape cancels the row; Tab/Shift+Tab moves between editable batch fields, skipping ID and other read-only columns. |
| R06 | `BeginEditAsync`, `AddRecordAsync`, `DeleteRecordsAsync`, `SaveChangesAsync`, `CancelEditAsync`, `CancelChangesAsync`, `HasPendingChanges`, `CurrentEditContext` and `GetCurrentRecords` expose the same pipeline. `NewItemFactory` provides unique IDs, `NewRowPosition` inserts new records at the top/bottom of their sibling group, and subtree deletion can be confirmed. Batch save validates every staged row and publishes one complete transaction. Cancel restores staged deletes and their previous expansion state. |
| R06 | `EditStarting` and `DataChanging` can cancel operations; `DataChanged` reports committed Add/Update/Delete/Move records. Callback-amended drafts are validated again. Replacing the data source invalidates pending async edits and commits. Successful updates replace records and invoke `DataSourceChanged`; cancellation and rejected validation leave the source untouched. |
| R07 | `ShowCheckboxes`, `AutoCheckHierarchy`, `CanCheckRow`, bound `CheckedItems`, `GetCheckedRecords`, `SetRowCheckedAsync` and `CheckAllAsync`. Parents derive checked/mixed state from eligible descendants. Disabled branches are excluded. Checks persist across collapse, filters and pages; a checked lazy parent passes its check to loaded children. Header check-all applies to **all loaded rows**. Primary row/cell focus remains independent. |
| R08 | `AllowRowDragAndDrop`, `MoveRowAsync`, `IndentAsync`, `OutdentAsync`, `CanDrop`, cancellable `RowMoving` and post-commit `RowMoved`. Moves retain the full loaded subtree, clone the moved record, validate hierarchy cycles, and use the same data transaction callbacks as editing. The FlexCore toolbar/context menu and Move dialog offer keyboard alternatives; native drag shows Before/Inside/After destinations. Ctrl+Right/Left indents/outdents. Pending edits must be saved/cancelled first; sorted sibling reordering requires clearing sorting. Loaded provider rows are not fetched again or duplicated after a move. |
| R09 | `FrozenColumns` pins the first N **visible** columns; individual `TreeGridColumn.IsFrozen` / `FrozenPosition` and `FreezeColumnAsync` support Left/Right. The header context menu provides Freeze left / Freeze right / Unfreeze column. CSS sticky cells retain opaque backgrounds and header stacking; a ResizeObserver in the existing `legacy-scrollbar.js` measures actual widths after resize/visibility changes. Frozen offsets and the tree-column cursor follow the visible column order. |
| R11 | `TreeGridViewState` additionally round-trips checked IDs, the frozen count and per-column frozen overrides. |

Editing requires reference-type records with public writable edited properties and stable unique IDs. The default clone uses JSON serialization; supply `CloneFactory` for models needing a different deep-copy strategy. ID and parent ID are not cell-editable; parent changes use the move API. Root moves require a nullable/reference parent ID. Bind `DataSource` as `IEnumerable<T>` to receive replacement records; without binding, the control retains its committed copy until the host supplies a different data source. Hosts persist transactions through the callbacks. `NewItemFactory` must assign an unused ID. A newly added row remains reachable even under a filter that would exclude its initial values.

The legacy `CellEditTemplate` contract continues to work when built-in editing is not activated. Column `EditTemplate` receives the independent draft in built-in mode. Custom form/editor templates must participate in the supplied `EditContext`. When externally changing editing modes, save or cancel the current draft first. All new interactive bench fields, menus and dialogs are FlexCore components; only structural layout markup lives in the bench.

Hierarchy operations and saved check IDs cover loaded records. Cross-grid/touch dragging, remote transaction adapters, remote multi-selection and server-side subtree deletion are separate capabilities. Moving/deleting a loaded parent reports that parent to the host; persistence of unloaded descendants belongs to the host's data service. No new JavaScript module was added: native drag payloads, keyboard defaults and actual-width measurement extend the existing DOM bridge.

### TreeGrid remaining gaps

- Generic nested `ChildMapping`, a server-side sorting/filtering/paging adaptor, row/column virtualization, and root-group paging.
- Excel distinct-value/checklist filtering and the advanced GridControl filter builder.
- General rectangular cell-range selection and selection persistence across unloaded remote pages. Bound loaded-row hierarchy checkbox selection is implemented.
- Frozen rows, stacked headers, merged data cells and hierarchy aggregates. Left/right frozen columns are implemented.
- Print preview/page-layout UI and dedicated export events. Export does not fetch unloaded children; its PDF output is the existing GridExporter implementation.
- Full persistence of remote children, pending host-owned edits and every layout setting. Persisted IDs use the mapped ID property's invariant string representation and are restored against loaded records.

Paging counts visible rows, so a page may start with a child. Filtering preserves the stored expansion state while showing query results; collapse does not hide a matching descendant during an active filter. Typed filtering follows the column's declared `ColumnType`. Programmatic state restoration should also update bound host settings if those settings are supplied as parameters on every render.

## Shared TextBox correction

An automatically supplied `ValueExpression` previously selected Blazor's change-only `InputText` path even when `UpdateOnInput` or `OnKeyDown` was requested. The control now chooses its native input-event path for those requests and notifies the cascading EditContext when the value is committed. This fixes live tree searches and filter inputs while preserving their FlexCore implementation.

## Verification

- 257 model/component regression checks, including tree mapping and cycles, cascade checks, rejected moves, typed comparisons, rename lifecycle, loading failures, collapse during loading, state restoration, export scope, live TextBox/EditContext updates, atomic edit validation/veto, subtree cancellation, reparenting, check/freeze state and stale async source replacement.
- 30 browser scenarios passed in Chromium and 30 in WebKit, using the actual Tester benches in a self-terminating local fixture. Coverage includes modifiers and navigation, mixed checks, lazy retry/cache, edit text/validation, native drag/drop, 10,000-node virtualization, custom-sort priority, numeric AND/OR menus, JSON state, real CSV/XLSX/PDF downloads, per-keystroke inline/dialog/batch editing, Tab/Shift+Tab, validation, server veto, add/delete/cancel, positional drag, and frozen columns after horizontal scroll, resize and hide/show.
- 83 existing-control browser compatibility checks passed, including DropDownTree selection/search and the controls using shared TextBox behavior.
- FlexCore.Showcase and FlexKitTester builds passed with existing warnings only.
