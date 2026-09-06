# FlexCore gap analysis against Syncfusion Blazor

Audit date: **5 September 2026**. FlexCore version: **0.2.32**, targeting .NET 10. Baseline: current working tree, including uncommitted controls, over commit `1902549e01c620e56a09153dc3b080feaf005c01`.

**Grid is the closest match. Chart interaction and axis configuration, TreeGrid's data/editing engine, and document editing have much larger gaps. Word/DOCX editing is a missing dedicated counterpart.** Several apparent chart capabilities are also ineffective settings or incomplete renderers, rather than features that merely need a better demo.

Implementation follow-up: [EditorControl's first parity increment](/Users/wadood/projects/VBToCSharp/FlexCore/docs/editor-parity-progress.md) adds shared editing behaviors and a dedicated bench. The baseline findings below remain the audit snapshot; the follow-up records what has since changed.

Document follow-up: [PDF Viewer and Spreadsheet's first parity increment](pdf-spreadsheet-parity-progress.md) adds occurrence search/bookmarks, fixes visible freeze behavior, and integrates worksheet filtering with dedicated bench coverage.

Tree follow-up: [TreeView and TreeGrid parity progress](tree-parity-progress.md) records bound tree state, lazy loading, editing, drag/drop, virtualization, typed hierarchy filters, ordered sorting, paging and export. Its second increment adds built-in inline/dialog/batch editing, hierarchy checkboxes, reparenting/indent/outdent, and left/right frozen columns. The follow-up records remaining gaps.

This analysis compares the actual FlexCore implementation with Syncfusion's current [Blazor catalog][sf-catalog], [documentation][sf-intro], and the component API references below. It does not carry forward the earlier Telerik audit's classifications. “Tree” covers TreeView, TreeGrid, and the related TreeMap visualization. “Documents” covers PDF Viewer, Spreadsheet, Word/DOCX editing, and a separate assessment of document-processing SDKs.

## How to read the findings

| Status | Meaning |
| --- | --- |
| Present | A concrete implementation exists for the stated capability. This is not certification of every interaction, browser, or feature combination. |
| Partial | Useful support exists, but the stated Syncfusion contract or workflow needs more work. A host template or underlying package alone does not establish UI parity. |
| Missing | No corresponding public contract and implementation were found in the inspected component. |
| Defect | An exposed setting or mode does not deliver its advertised behavior. Render probes are identified separately from source-only findings. |
| Verify | Evidence is insufficient to make a fair comparison; excluded from confirmed parity gaps. |

Most findings are based on source and API inspection. Seven targeted Blazor `HtmlRenderer` comparisons were also run against the actual ChartControl: six reproduced ineffective settings/modes, and a positive control showed that changing Y-axis limits does change output. Syncfusion packages were not installed or executed. No performance, file-fidelity, accessibility-compliance, or cross-browser equivalence claim is made.

The detailed tables contain **100 grouped capability checks: 16 Present, 43 Partial, 31 Missing, 6 Defect and 4 Verify**. These are capability groups, not counts of controls or individual API members. They should not be converted into a parity percentage.

## Overall assessment

| Area | FlexCore starting point | Main work required |
| --- | --- | --- |
| DataGrid | Extensive local/provider querying, filtering, editing, virtualization, state and export | Fill targeted workflow/API gaps; test supported combinations |
| Charts and StockChart | Many SVG renderers; dedicated financial range control | Correct ineffective settings; build richer axes, interactions, events and analytical features |
| TreeView | Local hierarchical nodes, basic selection/checking, icons and keyboard handling | Lazy data, bound multi/check state, editing, drag/drop, virtualization and accessibility semantics |
| TreeGrid | Self-referencing hierarchy, local sort/filter, expansion and custom cell editors | Share Grid querying/editing/export machinery and add hierarchy-specific operations |
| PDF Viewer | Real rendering, search, text selection, basic form editing and annotations | Document navigation, on-page forms, annotation lifecycle, page organization and richer APIs |
| Spreadsheet | ClosedXML workbook editing and a bounded table viewport | Excel-style viewport, more workbook UI, richer editing contracts and format conversion |
| Word/DOCX editor | Existing HTML/block editors are useful UI foundations | New document model, Word import/export and layout engine; dedicated control |
| Document-processing SDKs | ClosedXML and limited PDF functionality | Separate engine/service decisions for Word, PDF and PowerPoint |

## 1. DataGrid

Evidence: [GridControl][fc-grid], [filtering][fc-grid-filter], [provider contracts][fc-provider-model], [provider execution][fc-provider], [columns][fc-columns], [state][fc-grid-state], and [export models][fc-export]. Syncfusion comparisons use its [Grid API][sf-grid-api], [column API][sf-column-api], and the feature-specific references in the table.

| ID | Capability | Status | Actual gap or existing coverage |
| --- | --- | --- | --- |
| G01 | Local, dynamic and generated columns | Present | Collection binding, dynamic columns and column generation exist. A new grid component is unnecessary. |
| G02 | Remote query/data access | Partial | Async range providers, cancellation, stale-result handling, remote distinct values, group children, aggregates and full-query export exist. Packaged OData/WebAPI/GraphQL adapters and a standardized provider CRUD adapter are absent. Hosts currently supply that integration. Compare [Syncfusion adaptors][sf-adaptors]. |
| G03 | Single and ordered multi-sort | Present | Ordered descriptors, priority indicators and an interactive sort dialog exist. This is not a missing feature. See [multi-sort implementation][fc-multisort]. |
| G04 | Per-column custom ordering | Missing | Local sorting uses the library's comparer; no public per-column comparer equivalent to `GridColumn.SortComparer` was found. A host can still sort provider results. |
| G05 | Filter row, menu and Excel checklist | Present | Typed filters, Contains-only row option, dual conditions, Excel value selection, templates and expression filters exist. Operator grammar and empty/null behavior still need comparative cases before claiming exact equivalence. See [filter documentation][sf-grid-filter]. |
| G06 | Inline, dialog and batch editing | Present | Edit drafts, built-in editors, custom templates, commit/cancel and validation/EditContext paths exist. Earlier reported typing issues are not evidence that this entire capability is missing. See [row editing][fc-grid-edit] and [validation][fc-grid-validation]. |
| G07 | Batch change-set API | Partial | Batch editing exists, but a public change-set retrieval/application contract comparable to `GetBatchChangesAsync` / `ApplyBatchChangesAsync` was not found. UI batching and application-controlled transactions are different requirements. |
| G08 | Foreign-key columns | Partial | Edit options, row-aware option providers, value/label mapping and templates can display/edit lookups. No dedicated foreign collection/key/display-field contract integrates display, filter and sorting semantics. Compare [foreign-key columns][sf-foreign]. |
| G09 | Row and column virtualization | Present | Both are implemented, including retention of active/frozen columns. Restrictions are explicit. Do not count every unsupported combination as a Syncfusion gap; see the limitations discussion below. |
| G10 | Infinite scrolling as a separate mode | Missing | No public append/block-cache mode equivalent to `EnableInfiniteScrolling` was found. Existing virtual windowing should be retained and credited. |
| G11 | Frozen columns and rows | Partial | Left and right frozen columns exist. Frozen data rows and an interactive freeze-line feature are absent. [Column positioning][fc-column-features] already handles both sides. |
| G12 | Resize, reorder, chooser and auto-fit | Present | Existing column features cover these workflows. |
| G13 | Arbitrarily nested stacked headers | Partial | [Header bands][fc-header-bands] support one parent level over leaf columns; their model has no recursive children. Deeper header nesting requires a model/rendering extension. |
| G14 | Cell merging and automatic spans | Missing | No data-cell row/column span or merge contract was found. Header-band colspan does not provide data-cell merging. Compare [Syncfusion column spanning][sf-spans] and `AutoSpan` / `MergeCellsAsync`. |
| G15 | Selection and keyboard navigation | Present | Row/cell selection, multiple selection, reveal/select methods and key mappings exist. Fine-grained interaction parity remains a browser-test task. |
| G16 | Rectangular clipboard and autofill | Partial | Cell-context copy/cut/paste handles an active cell; mass editing also exists. No rectangular TSV paste transaction or drag-fill workflow was found in GridControl. Spreadsheet's separate TSV support does not make Grid support it. [Clipboard path][fc-grid-clipboard]. |
| G17 | Grouping and aggregates | Partial | Grouping, group footers and Sum/Average/Count/Min/Max exist, including provider aggregate results. A public local custom-aggregate callback is absent. [Aggregate implementation][fc-grid-aggregates]. |
| G18 | Row drag/drop between grids | Partial | Local row reordering exists for mutable lists and excludes grouped mode. No target-grid transfer/reparent transaction contract was found. [Reorder eligibility][fc-row-reorder]. |
| G19 | Detail rows and templates | Present | Detail, row, cell, header, toolbar, pager and empty-state customization exist, subject to virtualization/provider restrictions. |
| G20 | Adaptive presentation and dialogs | Partial | Stacked/auto row presentation exists. The full-screen mobile filter/sort/chooser/edit workflow described in [Syncfusion adaptive UI][sf-adaptive] is not equivalent to merely switching row layout. |
| G21 | Export and printing | Partial | XLSX/CSV/TSV/HTML/XLS/JSON/PDF, numeric formatting, widths, alignment, formulas and selected PDF decoration exist. The export model lacks general arbitrary per-cell images, hyperlinks, merged layouts and rich multi-grid export composition. Do not classify all export styling as missing. [Syncfusion export options][sf-grid-export]. |
| G22 | Persisted grid state | Present | Capture/apply state and layout storage already exist. They are FlexCore contracts, not binary-compatible Syncfusion persistence payloads. |

**Virtualization comparison needs a matrix, not a blanket finding.** FlexCore's [flat provider validation][fc-provider] rejects variable row heights, custom row templates, detail rows, adaptive layouts and inline row editing in that mode; lazy provider grouping has a different detail-row path. Syncfusion's [virtual scrolling documentation][sf-virtualization] also excludes several combinations, including batch editing, detail/row templates and autofill for row virtualization, and further combinations for column virtualization. Its clipboard and drag/drop operations have viewport restrictions. These are product-specific constraints that must be tested with the same configuration.

Recommended Grid implementation order: rectangular clipboard/transaction semantics; foreign-key display/filter contracts; custom comparers/aggregates; frozen rows and nested headers; export extension hooks; protocol adapters where applications need them.

## 2. Charts and related controls

Evidence: [ChartControl dispatch][fc-chart-dispatch], [line/area renderer][fc-chart-lines], [chart models][fc-chart-models], [enums][fc-chart-enums] and [StockChartControl][fc-stock]. Compare [Syncfusion chart series][sf-chart-series], [axes][sf-chart-axis-api], [chart API][sf-chart-api], and the interaction documentation below.

| ID | Capability | Status | Actual gap or existing coverage |
| --- | --- | --- | --- |
| C01 | Basic chart families | Present | Bar/column, line, spline, step, area, pie/donut, scatter/bubble, financial, range and several specialized SVG renderers exist. Spline has curve generation; Pareto and bar-line combo have explicit branches. Enum count is not a parity score. |
| C02 | Treemap rendering | Defect | `ChartType.Treemap` has no dedicated dispatch branch and falls through to bar rendering. A render probe produced identical markup for Treemap and Bar. Hierarchical layout and drill-down are missing. Compare [Syncfusion TreeMap][sf-treemap]. |
| C03 | Stacked area rendering | Defect | `StackedArea` follows the area renderer without cumulative stacking. A two-series render probe produced identical markup for Area and StackedArea. |
| C04 | Axis range and styling settings | Defect | In the line chart, changing X-axis Min/Max or Y-axis ShowGridLines leaves output unchanged. Source also does not apply `ChartAxis.Format`. Y-axis Min/Max do work and served as the positive control. |
| C05 | Legend positioning | Defect | `ChartLegend.Position` is not consumed; Top and Bottom render identically. Legend visibility is implemented. |
| C06 | Animation switch | Defect | `Animate` is exposed but not consumed by rendering or a lifecycle animation path. True/false render identically. |
| C07 | Data binding and point semantics | Partial | Explicit series/data-point lists and legacy label/value-field binding exist. No general per-series X/Y field mapping, nullable-point policy or rich series metadata contract matches Syncfusion's `XName`, `YName`, and `EmptyPointSettings`. |
| C08 | Numeric, date and logarithmic axes | Partial | Axis titles and some Y-axis scale settings work. Line X positions are equally spaced by index; `DateValue` does not provide a continuous date axis there. No general logarithmic/date/category axis strategy with intervals and label policies exists. [Axis configuration][sf-chart-axis]. |
| C09 | Multiple axes and plot panes | Missing | No named-axis collection, series-to-axis binding, row/column plot layout or independently scaled panes. Compare the [axis API][sf-chart-axis-api]. |
| C10 | Tooltips and crosshairs | Partial | Native SVG title tooltips exist. Shared/template tooltips, crosshairs and programmatic show/hide APIs are absent. |
| C11 | Point/series selection and highlighting | Missing | No selection state or point/legend click event contract. See [Syncfusion selection][sf-chart-selection]. |
| C12 | Zoom, pan and range selection | Partial | StockChart has date range, zoom and pan methods. General ChartControl lacks pointer/pinch/wheel zoom, pan, selection zoom and axis viewport state. See [Syncfusion zooming][sf-chart-zoom]. |
| C13 | Legend interaction | Missing | No interactive series visibility toggle, selection or event-driven legend customization. |
| C14 | Analytical overlays | Missing | No trendline, error-bar or technical-indicator configuration/render pipeline. Compare [trendlines][sf-trendlines] and [technical indicators][sf-indicators]. |
| C15 | Annotations and axis decorations | Missing | No coordinate-bound annotation collection, strip lines or multi-level axis labels. |
| C16 | Financial chart depth | Partial | Dedicated stock control, OHLC/candlestick/high-low values and a navigator exist. Indicators, stock events, independent volume/indicator panes and general stock-series configuration are absent. |
| C17 | Additional chart families | Missing | Polar, Smith and true 3D chart counterparts were not found. Basic Radar exists; it is not a Polar/Smith implementation. See [Polar][sf-polar], [Smith][sf-smith], and [3D Charts][sf-3d]. |
| C18 | Print/export and chart lifecycle methods | Missing | No chart image/PDF export or print API, point interaction callbacks, or chart-specific render events. Compare [Syncfusion chart printing/export][sf-chart-export]. |
| C19 | Accessible chart navigation | Partial | Chart text is rendered, but no point/series keyboard traversal or corresponding interactive chart accessibility model was found. Compare [Syncfusion accessibility][sf-chart-accessibility]. This is not a WCAG compliance verdict. |
| C20 | Large-data update/render strategy | Verify | SVG output exists, but no controlled runtime comparison was performed. Benchmark large series, frequent updates, memory and frame times before assigning performance parity. |
| C21 | Interactive chart-building UI | Missing | Generic WizardControl and ChartControl exist, but there is no data/series/type configuration UI corresponding to [Syncfusion Chart Wizard][sf-chart-wizard]. |
| C22 | General range navigator | Partial | StockChart's two range sliders and RangeSliderControl offer reusable parts. There is no standalone chart-backed navigator with a general axis/data model corresponding to [Syncfusion Range Navigator][sf-range]. |

Heatmap, sparkline, bullet, Sankey and gauges already have modes or controls. Their existence should be credited; this audit does not certify each specialized feature set. Missing specialized methods are usually extensions to shared rendering and interaction code, not justification for duplicating the renderer in another wrapper.

**Correct chart behavior before expanding the catalog:** implement or remove ineffective promises, then establish typed coordinates/axis transforms, hit testing, selection, event callbacks, tooltips and export. Those foundations also enable the stock, range and specialized chart work.

### Reproduced chart findings

Each comparison used the same two series: A/B values of 10/20 and 5/15. The actual component was rendered with only the stated parameter changed.

| Probe | Result |
| --- | --- |
| Treemap versus Bar | Identical markup — incorrect renderer fallback |
| StackedArea versus Area | Identical markup — no stacked-area geometry |
| Animate true versus false | Identical markup; parameter unused in source |
| Legend Top versus Bottom | Identical markup; position unused in source |
| Line XAxis range 0–10 versus 100–1000 | Identical markup |
| Line YAxis ShowGridLines true versus false | Identical markup |
| Positive control: Line YAxis range 0–10 versus 100–1000 | Different markup, as expected |

Static markup equality alone does not prove every client behavior. Here it is paired with inspection of the corresponding dispatch, model and render paths; ChartControl has no separate client animation/interaction implementation consuming those ignored parameters.

## 3. TreeView

Evidence: [TreeViewControl][fc-treeview] and [TreeNode model][fc-tree-model]. Compare [Syncfusion TreeView API][sf-treeview-api], [data binding][sf-tree-data], and the referenced behavior guides.

| ID | Capability | Status | Actual gap or existing coverage |
| --- | --- | --- | --- |
| T01 | Local hierarchy, icons and basic navigation | Present | Nested TreeNode lists, expansion, selection, icons, disabled nodes and arrow/Home/End/Enter/Space handling exist. |
| T02 | Generic binding and remote/lazy children | Partial | Hosts can build TreeNode lists and react to expansion. `HasChildren` only means `Children.Count > 0`, so an unloaded branch has no independent child-existence state. No mapped self-referencing data/provider contract exists. |
| T03 | Multi-selection binding | Partial | `AllowMultiSelect` toggles individual flags. No bound selected-node collection, Ctrl/Shift range semantics or complete collection-change event contract was found. |
| T04 | Checkbox hierarchy | Partial | Individual checks work. No mixed state, parent/child cascade policy, bound checked collection or checked-change callback. Compare [Syncfusion checkboxes][sf-tree-checks]. |
| T05 | Inline node editing | Missing | No built-in F2/rename editor or validation/commit/cancel lifecycle. Compare [node editing][sf-tree-edit]. |
| T06 | Drag/drop and reparenting | Missing | No node drag operation, allowed-target policy or reorder/reparent callback. Compare [drag/drop][sf-tree-drag]. |
| T07 | Node templates and sorting | Partial | Icons, text and styles are configurable. No node RenderFragment or public node ordering/comparer contract was found. Host-prepared order remains possible. |
| T08 | Virtualization | Missing | All expanded nodes are rendered recursively. No window/provider mechanism equivalent to `EnableVirtualization`. |
| T09 | Programmatic state and operations | Partial | Hosts can mutate node flags. Dedicated add/remove/edit/check-all/ensure-visible APIs and selected/checked/expanded ID collections are absent. |
| T10 | Accessibility semantics | Partial | Keyboard handling exists, but rendered output lacks tree/treeitem/group roles and level/expanded/selected/checked ARIA state. Compare [Syncfusion TreeView accessibility][sf-tree-accessibility]. |
| T11 | Encoded text / HTML sanitization | Missing | Node text is emitted as `MarkupString`. No explicit encoded-text default or sanitization option comparable to `EnableHtmlSanitizer` was found. This matters when node labels come from untrusted data. [Render path][fc-tree-text]. |

Extend the existing control. Priority should be stable node IDs and bound state, accessible rendering, lazy children, then checkbox cascade and editing/drag operations. Do not replace working basic keyboard navigation while doing that work.

## 4. TreeGrid

**TreeGridControl is a separate implementation; it does not inherit GridControl's complete feature set.** Its name and comments must not be used as evidence of Grid parity.

Evidence: [TreeGrid parameters and operations][fc-treegrid], [body rendering][fc-treegrid-body], [column model][fc-treegrid-column], and [filter evaluation][fc-treegrid-filter]. Compare [Syncfusion TreeGrid API][sf-treegrid-api] and [feature tour][sf-treegrid-tour].

| ID | Capability | Status | Actual gap or existing coverage |
| --- | --- | --- | --- |
| R01 | Self-referencing hierarchy and expansion | Present | IdMapping/ParentIdMapping, a tree column, expand/collapse all/by level and icons/templates exist. |
| R02 | Child collection and remote binding | Partial | Local flat self-referencing data works. No generic ChildMapping, async child-provider/paging contract or equivalent of LoadChildOnDemand/HasChildMapping. |
| R03 | Row/column virtualization and paging | Missing | The body enumerates all visible expanded nodes. GridControl's provider/windowing implementation is not used here. Compare [TreeGrid virtualization][sf-treegrid-virtual]. |
| R04 | Ordered multi-sort | Partial | Sorting is implemented, but selecting a column clears other sort directions. The sort engine uses one active column. |
| R05 | Typed/Excel filters and hierarchy policy | Partial | Existing filters perform case-insensitive string Contains. Matches include both ancestors and descendants. No typed operator/menu/checklist engine or selectable Parent/Child/Both/None policy. Compare [TreeGrid filtering][sf-treegrid-filter]. |
| R06 | Built-in editing, CRUD and validation | Partial | CellEditTemplate/CellEditPredicate allow host editors. There is no shared built-in row/dialog/batch draft, validation/EditContext and add/delete/commit pipeline. Compare [batch editing][sf-treegrid-edit]. |
| R07 | Selection and hierarchy checkboxes | Partial | Row/cell cursor selection exists. Bound multiple records/cells and hierarchical checkbox selection are absent. SelectionMode alone does not supply those workflows. |
| R08 | Indent/outdent and row drag/drop | Missing | No reparent/move command or transaction lifecycle corresponding to SetIndentRecordAsync/SetOutdentRecordAsync. |
| R09 | Advanced columns and aggregates | Partial | Basic sizing, sorting/filtering and column options exist. Frozen rows/columns, multi-level headers, data-cell spanning and tree aggregate rows were not found. |
| R10 | Export and printing | Missing | No built-in TreeGrid XLSX/CSV/PDF/print workflow. Shared Grid export code offers reuse, but hierarchy/indentation and expand-state export semantics still need definition. |
| R11 | Persisted state and event breadth | Partial | Selection, activation, expansion and toolbar callbacks exist. No full persisted query/layout/edit state or cancellable action lifecycle comparable to the combined Syncfusion API. |

Best reuse path: extract shared query descriptors, editors/validation, selection, export and column metadata from Grid infrastructure, then integrate a hierarchy-aware visible-row provider. A second independent implementation of these engines would increase maintenance without improving parity.

## 5. PDF Viewer

Evidence: [PdfViewerControl][fc-pdf], [PDF models][fc-pdf-models] and [PDF rendering/editing bridge][fc-pdf-js]. This control uses PDF.js for viewing and PDF-Lib for edits. Compare [Syncfusion PDF Viewer overview][sf-pdf-overview] and [PdfViewerBase API][sf-pdf-api].

| ID | Capability | Status | Actual gap or existing coverage |
| --- | --- | --- | --- |
| P01 | Open, navigate, zoom and rotate | Present | Real byte/URL/file loading, page navigation, zoom and rotation exist. Password is an exposed viewing parameter; encrypted editing is a separate limitation. |
| P02 | Text search and selection | Partial | Search results and selectable text exist. Public FindAsync takes no query argument and uses private UI state; query/case/next/previous search methods are missing. |
| P03 | Continuous document view | Missing | The component renders one page canvas at a time. No continuous multi-page viewport, page virtualization or spread layout contract was found. |
| P04 | Thumbnails, bookmarks and links | Missing | Sidebar content covers search/forms/notes, not thumbnail/bookmark/outline navigation. No PDF hyperlink navigation layer was found. |
| P05 | Interactive forms | Partial | Text, checkbox and single-choice fields can be edited through the sidebar. No on-page field overlays. Unsupported field types and multi-select fields are read-only; XFA editing is explicitly rejected. |
| P06 | Form designer and data APIs | Missing | No field creation/placement, tab-order/design mode, form data import/export or field-validation lifecycle matching Syncfusion's [form designer][sf-pdf-forms]. |
| P07 | Annotation types | Partial | Notes and highlights exist. Shape, ink, stamp, free-text and measurement authoring need implementation. |
| P08 | Annotation lifecycle | Partial | Current-session added notes/highlights can be removed; existing annotations are inspected. No general edit-existing, annotation history, annotation data import/export or comprehensive event API. |
| P09 | Signatures | Missing | SignatureControl exists separately, but no PDF signature-placement/field workflow integrates it. Handwritten appearance and cryptographic PDF signing must be tracked separately. |
| P10 | Page organization and redaction | Missing | No insert/delete/move/extract page workflow or apply-redactions API. These are explicit methods in the current Syncfusion viewer API. |
| P11 | Save/download/print | Present | Edited bytes can be saved and downloaded, and printing exists. Advanced file fidelity and font coverage have not been certified. |
| P12 | Document compatibility and event surface | Partial | Basic errors/load/page callbacks exist. Encrypted/XFA editing, unsupported form types and appearance-font coverage need explicit capability contracts and fixtures. No full cancellable annotation/form lifecycle exists. |

Syncfusion's own [desktop/mobile feature matrix][sf-pdf-overview] has differences, including keyboard and text-selection limitations on mobile. Do not use its desktop capabilities as evidence of universal mobile parity. PDF-Lib/PDF.js capability or preservation alone does not imply that FlexCore exposes an interactive feature.

## 6. Spreadsheet

Evidence: [SpreadsheetDocument][fc-sheet-model], [SpreadsheetControl][fc-sheet] and [viewport styles][fc-sheet-css]. The public `Document.Workbook` exposes ClosedXML, so workbook-model operations available through that object must not be described as fundamentally impossible. The question is whether FlexCore supplies a supported, integrated UI and command contract.

Compare [Syncfusion Spreadsheet overview][sf-sheet-overview], [API][sf-sheet-api], and [Blazor feature tour][sf-sheet-tour].

| ID | Capability | Status | Actual gap or existing coverage |
| --- | --- | --- | --- |
| S01 | XLSX open/save and editable cells | Present | Real workbook load/save, typed values, in-cell/formula-bar editing and formulas through ClosedXML exist. |
| S02 | Selection, clipboard and undo | Partial | Range selection, TSV copy/paste and undo/redo exist. Rich-format clipboard interchange, cut/move semantics and complete Excel keyboard selection behavior need work. |
| S03 | Scrolling and virtualization | Partial | A bounded row/column window is controlled by buttons and keyboard navigation. No scroll-driven virtual worksheet viewport was found. A configurable row limit is not proof that all those rows are rendered efficiently. |
| S04 | Freeze command behavior | Defect | Freeze writes workbook freeze metadata, but the viewport never consumes it to keep selected data rows/columns fixed. Header CSS sticking is a separate feature. Source-confirmed FlexCore defect; Syncfusion Blazor freeze API equivalence still needs verification. |
| S05 | Formatting, merge and dimensions | Partial | Basic font/color/alignment/number formatting and merge/unmerge exist. No integrated row/column resize workflow or complete Excel-style formatting UI. |
| S06 | Sorting and filtering | Partial | Range sorting exists. No interactive worksheet filtering/filter-condition model integrated into the displayed rows. |
| S07 | Conditional formatting | Missing | No conditional-rule authoring or rule-aware cell rendering path. ClosedXML persistence is not an interactive rule editor. Syncfusion exposes ConditionalFormatAsync and related APIs. |
| S08 | Hyperlinks and images | Missing | No corresponding cell-display/editing UI. Underlying workbook storage may preserve them; this audit did not test retention. Syncfusion exposes hyperlink and image capabilities. |
| S09 | Formulas and named ranges | Partial | Formula strings and ClosedXML calculations work. No formula autocomplete/function help or named-range management UI; no verified claim of matching either Excel's or Syncfusion's function coverage. |
| S10 | Fill and sheet management | Partial | Fill-down and add/rename/delete sheets exist. Pattern autofill, drag fill, and integrated move/duplicate/hide/unhide operations need work. Some operations can already be reached through ClosedXML. |
| S11 | Protection | Partial | Existing protected/locked cells are enforced and several mutations reject protected sheets. No integrated protection creation, permissions and password workflow. |
| S12 | File formats and print/export | Partial | Current control opens/saves XLSX. No corresponding XLS/CSV/PDF conversion/print contract, unlike the formats documented for Syncfusion Spreadsheet. Grid's exporters do not automatically export a workbook. |
| S13 | History scalability | Verify | Each mutation snapshots the full workbook into XLSX bytes, with a configurable history limit. This is a measurable scaling concern, not a benchmark result. Compare equivalent workbook sizes before choosing an incremental undo design. |
| S14 | Embedded charts and data-validation UI | Verify | FlexCore lacks these integrated workflows. Syncfusion's tour mentions them broadly, but the inspected Blazor API/overview did not establish the exact authoring contract. Verify the current Blazor package before counting them as confirmed vendor parity gaps. |

The current Syncfusion Spreadsheet offering is documented separately from its JavaScript Spreadsheet. Features found only in JavaScript, ASP.NET Core or desktop documentation have not been silently credited to Blazor.

## 7. Word/DOCX, rich text and block editing

FlexCore's [EditorControl block model][fc-editor-model] and [EditorLite output model][fc-editor-lite-model] support editing tasks, but neither is a Word document engine. `OpenDocumentHtml` is not DOCX or an ODT package.

| ID | Capability | Status | Actual gap or existing coverage |
| --- | --- | --- | --- |
| W01 | HTML/block text editing | Present | Existing editors provide reusable text/formatting, block, selection and pagination foundations. They must not be listed as wholly missing controls. |
| W02 | Dedicated Word/DOCX document editor | Missing | No control/model that loads, edits and round-trips Word documents was found. Compare [Syncfusion DOCX Editor][sf-docx-overview]. |
| W03 | Word layout and document structure | Missing | No Word-compatible sections, page geometry, header/footer and table layout model. Existing visual pagination does not establish Word layout fidelity. |
| W04 | Word review features | Missing | No Word comments/revisions/track-changes model or accept/reject pipeline. Existing application annotations would need explicit conversion semantics. |
| W05 | Word fields and structured content | Missing | No integrated Word bookmarks, TOC, field updating or form-field import/export contract. |
| W06 | DOCX import/export and conversion | Missing | No Word import/export engine or reliable Word-format conversion workflow. HTML/RTF-like exports from EditorLite do not satisfy this requirement. |
| W07 | Word editing API modules | Missing | No document editor/selection/history/search module contract equivalent to the [Syncfusion document editor API][sf-docx-api]. Existing editor methods can inform naming and events, not file compatibility. |
| W08 | Rich Text Editor / BlockEditor parity | Verify | Existing analogues are present. Tables, embedded content, paste cleanup, collaboration and detailed event parity need a separate editor-focused audit. No full Rich Text Editor or BlockEditor parity verdict is asserted here. |

A DOCX editor requires an engine decision before UI work. Reuse FlexCore dialogs, ribbon/toolbar, inputs, tree navigation and editor affordances; define a document model and import/layout/export backend. Syncfusion itself distinguishes its SFDT client representation and Word import/export paths, with some operations requiring supporting services. See [opening documents][sf-docx-open] and [deployment/dependency explanation][sf-docx-tour]. A thin wrapper around a contenteditable surface cannot supply those guarantees.

## 8. Document processing is a separate scope

The catalog lists .NET document libraries alongside document UI. These are processing engines, not additional Blazor controls.

| Engine family | FlexCore coverage | Remaining work for comparable scope |
| --- | --- | --- |
| [.NET Word / DocIO][sf-word-sdk] | No general Word engine found | DOCX read/write, styles/layout, fields, mail merge, and conversion services |
| [.NET PDF][sf-pdf-sdk] | PDF.js viewing, PDF-Lib edits and limited grid/report PDF paths | General document creation/manipulation, broader text/font/layout handling, protection, signing, redaction/OCR and standards support, where required |
| [.NET Excel / XlsIO][sf-excel-sdk] | ClosedXML is a substantial existing workbook foundation | Verify formula/format fidelity and conversion needs; add missing services around that foundation rather than assuming a new workbook engine is needed |
| [.NET PowerPoint][sf-ppt-sdk] | No presentation file-processing engine found | PPTX creation/editing, layouts, media, charts and conversion; this is not a missing native PowerPoint editor in the inspected Blazor UI catalog |

The current catalog also lists Smart Data Extraction and Markdown processing. These adjacent services, plus a comprehensive report-designer comparison, are outside this focused audit. Licensing, deployment size, server/browser execution and document-fidelity fixtures belong in any later engine selection; no vendor or new dependency was selected here.

## 9. Representative API crosswalk

These are behavioral mappings, not proposals to copy every Syncfusion method name. Where semantics differ, an alias would conceal the gap.

### Grid

Reference: [SfGrid API][sf-grid-api]. FlexCore locations: [public operations][fc-grid-public], [state][fc-grid-state], [validation][fc-grid-validation], and [multi-sort][fc-multisort].

| Syncfusion API | FlexCore counterpart | Difference |
| --- | --- | --- |
| SortColumnAsync / SortColumnsAsync | SortByColumnAsync; ordered sort state; OpenSortDialogAsync | Core behavior exists; signatures and descriptor models differ |
| FilterByColumnAsync | FilterByColumnAsync; advanced criteria/filter descriptors | Convenience string overload is narrower than Syncfusion's typed operator/value signature |
| ClearFilteringAsync | ClearFilteringAsync / ClearColumnFilterAsync | Equivalent operation family exists |
| SelectRowAsync / GetSelectedRecordsAsync | SelectRowIndexAsync / GetSelectedRecordsAsync | Index and filtered/provider visibility semantics should be tested |
| EditCellAsync / EndEditAsync | BeginEditCellAsync / EndEditAsync | Existing edit lifecycle; verify mode-specific behavior |
| GetEditContextAsync | ActiveEditContext | Existing validation context, different access form |
| GetBatchChangesAsync / ApplyBatchChangesAsync | No comparable public change-set contract found | Requires a transaction API, not a naming alias |
| GetPersistDataAsync / SetPersistDataAsync | GetStateAsync / SetStateAsync | Existing persistence; different payload schema |
| ExportToExcelAsync / ExportToPdfAsync | Existing Grid export methods | Rich vendor export-properties objects exceed current models |
| MergeCellsAsync / UnmergeCellsAsync | None found | Requires span model and rendering/export behavior |

### Charts

References: [SfChart][sf-chart-api], [ChartSeries][sf-chart-series] and [ChartAxis][sf-chart-axis-api].

| Syncfusion API/configuration | FlexCore counterpart | Difference |
| --- | --- | --- |
| Series DataSource, XName, YName | ChartSeries.DataPoints; legacy LabelField/ValueFields | No equivalent general series field/axis mapping |
| XAxisName / YAxisName, axis collections | XAxis / YAxis | Only one axis pair model |
| ShowTooltip / ShowCrosshair / ClearSelection | Native titles only | Interactive state and methods missing |
| ExportAsync / PrintAsync | None found | Chart output service needed |
| Zoom settings and axis viewport | StockChart SetRangeAsync / ZoomAsync / PanAsync | Existing stock range behavior does not provide general chart gesture zoom |

### Trees

References: [SfTreeView][sf-treeview-api] and [SfTreeGrid][sf-treegrid-api].

| Syncfusion API/configuration | FlexCore counterpart | Difference |
| --- | --- | --- |
| TreeView SelectedNodes / CheckedNodes / ExpandedNodes | Flags on TreeNode; SelectedNode | No full collection binding/notification contract |
| TreeView BeginEditAsync / EnsureVisibleAsync | None found | Editor and reveal behavior needed |
| TreeView AddNodes / RemoveNodes / CheckAllAsync | Host mutates Nodes | No integrated public operation/event lifecycle |
| TreeGrid ExpandAllAsync / ExpandAtLevelAsync | Same operation family exists | Existing implementation |
| TreeGrid LoadChildOnDemand / HasChildMapping | Local hierarchy only | Provider and unloaded-child state needed |
| TreeGrid EndEditAsync / GetBatchChangesAsync | Custom CellEditTemplate | Built-in transaction pipeline missing |
| TreeGrid SetIndentRecordAsync / SetOutdentRecordAsync | None found | Hierarchy mutation semantics needed |
| TreeGrid ExportToExcelAsync / ExportToPdfAsync | None found | Reuse shared export machinery with hierarchy options |

### Documents

References: [PDF API][sf-pdf-api], [Spreadsheet API][sf-sheet-api] and [DocumentEditor API][sf-docx-api].

| Syncfusion API | FlexCore counterpart | Difference |
| --- | --- | --- |
| PDF LoadAsync / GoToPageAsync / ZoomAsync | OpenAsync / GoToPageAsync / SetZoomAsync | Core operations exist; FlexCore zoom is a scale factor, not a percentage |
| PDF SearchTextAsync / SearchNextAsync | FindAsync() | UI search exists, but no query argument or next/previous API |
| PDF GetDocumentAsync / DownloadAsync / PrintAsync | SaveAsync / DownloadAsync / PrintAsync | Existing output operations; compatibility limits differ |
| PDF AddAnnotationAsync / EditAnnotationAsync | Private note/highlight handlers | Public annotation model/lifecycle missing |
| PDF AddFormFieldsAsync / SetFormDrawingModeAsync | None found | Form designer needed |
| PDF InsertPagesAsync / MovePagesAsync / RedactAsync | None found | Page editing/redaction commands needed |
| Spreadsheet OpenAsync / SaveAsStreamAsync | DocumentBytes / SaveAsync returning bytes | Existing file path, different API shape |
| Spreadsheet UpdateCellAsync / SelectRangeAsync | Document.SetCell / SetRange; SelectAsync/Selection | Models exist; integrated public commands/events can be improved |
| Spreadsheet CellFormatAsync / ConditionalFormatAsync | Document.Format / no integrated conditional formatting | Basic formatting exists; rules and renderer missing |
| Spreadsheet AddDefinedNameAsync / ProtectSheetAsync | Underlying ClosedXML Workbook/Sheet | Package access exists; supported UI/command wrappers missing |
| DocumentEditor OpenAsync / SaveAsync / SerializeAsync | None for Word | New document engine/control required |
| DocumentEditor Editor / Selection / EditorHistory / Search | Existing editor-specific selection/edit methods | No Word model compatibility |

## 10. Which work actually needs a new counterpart?

| Candidate | Classification | Reuse opportunity |
| --- | --- | --- |
| Word/DOCX editor | **Missing dedicated control and engine** within the requested document scope | FlexCore editor UI, dialogs, toolbar/ribbon and navigation; new Word model/backend required |
| Smith Chart | **Missing specialized renderer/control** in the broader chart family | Chart color, legend, export and interaction infrastructure; new impedance/admittance geometry |
| 3D Charts | **Missing rendering family** in the broader chart family | Data/series configuration; new 3D geometry, projection and interaction |
| Chart Wizard | **Missing dedicated configuration UI** | Grid/Spreadsheet selection, ChartControl, WizardControl and existing inputs |
| General Range Navigator | **Missing dedicated counterpart with partial reusable behavior** | StockChart range logic, RangeSliderControl and chart axes |
| TreeMap | **Incomplete existing mode; dedicated API optional** | First repair ChartType.Treemap; add hierarchy/layout/drill events before deciding whether to expose a separate TreeMapControl |
| PDF Viewer, Spreadsheet, TreeView, TreeGrid, StockChart | **Already existing controls** | Extend their implementations; avoid duplicate counterparts |
| Word/PDF/Excel/PowerPoint processors | **Engine/service work** | Keep independent of the UI-control inventory |

Dedicated wrappers for heatmap, sparkline, bullet or other already-rendered modes may improve API ergonomics, but do not by themselves close feature gaps. The table is a scoped inventory, not a claim that these are every missing control in Syncfusion's entire catalog.

## 11. Proposed delivery order and acceptance evidence

| Stage | Work | Evidence required before marking complete |
| --- | --- | --- |
| 1 — Correct exposed behavior | Chart renderer/settings defects; spreadsheet freeze behavior; TreeView encoded text and accessibility state | Different expected chart geometry/settings output; actual fixed rows during scrolling; accessible tree state and encoded-label fixtures |
| 2 — Complete common controls | TreeView lazy children/check/selection/edit; TreeGrid sort/filter/edit/provider/export; targeted Grid gaps | Real keyboard/pointer tests, hierarchical query fixtures, commit/cancel/validation cases, provider cancellation and export round-trips |
| 3 — Build chart foundations | Typed axes/panes, hit testing, tooltips, selection, zoom, events, export | Irregular date/numeric spacing, multiple scales, pointer and keyboard selection, zoom range persistence and image/PDF output fixtures |
| 4 — Deepen document controls | PDF navigation/forms/annotations/page operations; spreadsheet viewport/format/filter/protection APIs | PDF field/annotation/page fixtures; XLSX formulas/merges/styles round-trips; UI changes visible after commands and undo |
| 5 — Add missing families | Word engine/editor; Smith/3D/Wizard/general navigator where full chart-family scope is wanted | Separate control benches backed by real capabilities, including Word document-fidelity fixtures |

Each implemented control should have its own FlexKitTester bench, following the earlier testing direction. Shared features also need combination cases; one isolated demo per feature is insufficient for Grid editing/filtering/selection interaction. All host UI should continue to use FlexCore controls.

“Full parity” should be signed off against explicit behaviors and formats. An enum member, empty method, feature label or placeholder bench must not count as completion.

## Sources and local evidence

All Syncfusion links are official documentation, API references or product pages accessed for this audit. Product claims were checked against API/behavior documentation where available; uncertain Spreadsheet claims are explicitly withheld. The local links refer to the audited working tree, so line numbers may move as implementation continues.

[sf-catalog]: https://www.syncfusion.com/blazor-components
[sf-intro]: https://blazor.syncfusion.com/documentation/introduction
[sf-grid-api]: https://help.syncfusion.com/cr/blazor/Syncfusion.Blazor.Grids.SfGrid-1.html
[sf-column-api]: https://help.syncfusion.com/cr/blazor/Syncfusion.Blazor.Grids.GridColumn.html
[sf-adaptors]: https://blazor.syncfusion.com/documentation/data/adaptors
[sf-grid-filter]: https://blazor.syncfusion.com/documentation/datagrid/filtering
[sf-foreign]: https://blazor.syncfusion.com/documentation/datagrid/foreignkey-column
[sf-spans]: https://blazor.syncfusion.com/documentation/datagrid/column-spanning
[sf-adaptive]: https://blazor.syncfusion.com/documentation/datagrid/adaptive-layout
[sf-virtualization]: https://blazor.syncfusion.com/documentation/datagrid/virtual-scrolling
[sf-grid-export]: https://blazor.syncfusion.com/documentation/datagrid/templates-excel-export
[sf-chart-api]: https://help.syncfusion.com/cr/blazor/Syncfusion.Blazor.Charts.SfChart.html
[sf-chart-series]: https://help.syncfusion.com/cr/blazor/Syncfusion.Blazor.Charts.ChartSeries.html
[sf-chart-axis-api]: https://help.syncfusion.com/cr/blazor/Syncfusion.Blazor.Charts.ChartAxis.html
[sf-chart-axis]: https://blazor.syncfusion.com/documentation/chart/axis-customization
[sf-chart-selection]: https://blazor.syncfusion.com/documentation/chart/selection
[sf-chart-zoom]: https://blazor.syncfusion.com/documentation/chart/zooming
[sf-trendlines]: https://blazor.syncfusion.com/documentation/chart/trend-lines
[sf-indicators]: https://blazor.syncfusion.com/documentation/chart/technical-indicators
[sf-chart-export]: https://blazor.syncfusion.com/documentation/chart/chart-print
[sf-chart-accessibility]: https://blazor.syncfusion.com/documentation/chart/accessibility
[sf-polar]: https://blazor.syncfusion.com/documentation/chart/chart-types/polar
[sf-smith]: https://blazor.syncfusion.com/documentation/smith-chart/getting-started
[sf-3d]: https://blazor.syncfusion.com/documentation/3d-chart/getting-started-with-web-app
[sf-treemap]: https://blazor.syncfusion.com/documentation/treemap/drill-down
[sf-chart-wizard]: https://blazor.syncfusion.com/documentation/chart-wizard/getting-started
[sf-range]: https://blazor.syncfusion.com/demos/range-selector/range-navigator/
[sf-treeview-api]: https://help.syncfusion.com/cr/blazor/Syncfusion.Blazor.Navigations.SfTreeView-1.html
[sf-tree-data]: https://blazor.syncfusion.com/documentation/treeview/data-binding
[sf-tree-checks]: https://blazor.syncfusion.com/documentation/treeview/check-box
[sf-tree-edit]: https://blazor.syncfusion.com/documentation/treeview/node-editing
[sf-tree-drag]: https://blazor.syncfusion.com/documentation/treeview/drag-and-drop
[sf-tree-accessibility]: https://blazor.syncfusion.com/documentation/treeview/accessibility
[sf-treegrid-api]: https://help.syncfusion.com/cr/blazor/Syncfusion.Blazor.TreeGrid.SfTreeGrid-1.html
[sf-treegrid-tour]: https://www.syncfusion.com/blazor-components/blazor-tree-grid
[sf-treegrid-filter]: https://blazor.syncfusion.com/documentation/treegrid/filter
[sf-treegrid-edit]: https://blazor.syncfusion.com/documentation/treegrid/editing/batch-editing
[sf-treegrid-virtual]: https://blazor.syncfusion.com/documentation/treegrid/virtualization
[sf-pdf-overview]: https://help.syncfusion.com/document-processing/pdf/pdf-viewer/blazor/overview
[sf-pdf-api]: https://help.syncfusion.com/cr/blazor/Syncfusion.Blazor.SfPdfViewer.PdfViewerBase.html
[sf-pdf-forms]: https://blazor.syncfusion.com/documentation/pdfviewer-2/form-designer/ui-interactions
[sf-sheet-overview]: https://help.syncfusion.com/document-processing/excel/spreadsheet/blazor/overview
[sf-sheet-api]: https://help.syncfusion.com/cr/blazor/Syncfusion.Blazor.Spreadsheet.SfSpreadsheet.html
[sf-sheet-tour]: https://www.syncfusion.com/spreadsheet-editor-sdk/blazor-spreadsheet-editor
[sf-docx-overview]: https://help.syncfusion.com/document-processing/word/word-processor/blazor/overview
[sf-docx-api]: https://help.syncfusion.com/cr/blazor/Syncfusion.Blazor.DocumentEditor.SfDocumentEditor.html
[sf-docx-open]: https://help.syncfusion.com/document-processing/word/word-processor/blazor/opening-a-document
[sf-docx-tour]: https://www.syncfusion.com/docx-editor-sdk/blazor-docx-editor
[sf-word-sdk]: https://www.syncfusion.com/document-sdk/net-word-library
[sf-pdf-sdk]: https://www.syncfusion.com/document-sdk/net-pdf-library
[sf-excel-sdk]: https://www.syncfusion.com/document-sdk/net-excel-library
[sf-ppt-sdk]: https://www.syncfusion.com/document-sdk/net-powerpoint-library
[fc-grid]: /Users/wadood/projects/VBToCSharp/FlexCore/Grid/GridControl.razor.cs
[fc-grid-filter]: /Users/wadood/projects/VBToCSharp/FlexCore/Grid/GridControl.Filtering.cs
[fc-provider-model]: /Users/wadood/projects/VBToCSharp/FlexCore/Grid/GridItemsProvider.cs
[fc-provider]: /Users/wadood/projects/VBToCSharp/FlexCore/Grid/GridControl.ItemsProvider.cs:142
[fc-columns]: /Users/wadood/projects/VBToCSharp/FlexCore/Grid/GridColumn.cs
[fc-grid-state]: /Users/wadood/projects/VBToCSharp/FlexCore/Grid/GridControl.State.cs:28
[fc-export]: /Users/wadood/projects/VBToCSharp/FlexCore/Grid/GridExportModels.cs:96
[fc-multisort]: /Users/wadood/projects/VBToCSharp/FlexCore/Grid/GridControl.MultiSort.cs
[fc-grid-edit]: /Users/wadood/projects/VBToCSharp/FlexCore/Grid/GridControl.RowEditing.cs
[fc-grid-validation]: /Users/wadood/projects/VBToCSharp/FlexCore/Grid/GridControl.Validation.cs:17
[fc-column-features]: /Users/wadood/projects/VBToCSharp/FlexCore/Grid/GridControl.ColumnFeatures.cs:286
[fc-header-bands]: /Users/wadood/projects/VBToCSharp/FlexCore/Grid/GridColumnHeaderBand.cs:5
[fc-grid-clipboard]: /Users/wadood/projects/VBToCSharp/FlexCore/Grid/GridControl.razor.cs:5750
[fc-grid-aggregates]: /Users/wadood/projects/VBToCSharp/FlexCore/Grid/GridControl.razor.cs:3495
[fc-row-reorder]: /Users/wadood/projects/VBToCSharp/FlexCore/Grid/GridControl.razor.cs:1615
[fc-grid-public]: /Users/wadood/projects/VBToCSharp/FlexCore/Grid/GridControl.razor.cs:14986
[fc-chart-dispatch]: /Users/wadood/projects/VBToCSharp/FlexCore/Charts/ChartControl.razor:111
[fc-chart-lines]: /Users/wadood/projects/VBToCSharp/FlexCore/Charts/ChartControl.razor:667
[fc-chart-models]: /Users/wadood/projects/VBToCSharp/FlexCore/Charts/ChartModels.cs
[fc-chart-enums]: /Users/wadood/projects/VBToCSharp/FlexCore/Charts/ChartEnums.cs
[fc-stock]: /Users/wadood/projects/VBToCSharp/FlexCore/Charts/StockChartControl.razor:59
[fc-treeview]: /Users/wadood/projects/VBToCSharp/FlexCore/TreeViewControl.razor
[fc-tree-model]: /Users/wadood/projects/VBToCSharp/FlexCore/TreeViewModels.cs:10
[fc-tree-text]: /Users/wadood/projects/VBToCSharp/FlexCore/TreeViewControl.razor:103
[fc-treegrid]: /Users/wadood/projects/VBToCSharp/FlexCore/Grid/TreeGridControl.razor.cs
[fc-treegrid-body]: /Users/wadood/projects/VBToCSharp/FlexCore/Grid/TreeGridControl.razor:267
[fc-treegrid-column]: /Users/wadood/projects/VBToCSharp/FlexCore/Grid/TreeGridColumn.cs:88
[fc-treegrid-filter]: /Users/wadood/projects/VBToCSharp/FlexCore/Grid/TreeGridControl.razor.cs:1806
[fc-pdf]: /Users/wadood/projects/VBToCSharp/FlexCore/Pdf/PdfViewerControl.razor
[fc-pdf-models]: /Users/wadood/projects/VBToCSharp/FlexCore/Pdf/PdfViewerModels.cs
[fc-pdf-js]: /Users/wadood/projects/VBToCSharp/FlexCore/wwwroot/pdf-viewer.js
[fc-sheet-model]: /Users/wadood/projects/VBToCSharp/FlexCore/Spreadsheet/SpreadsheetDocument.cs
[fc-sheet]: /Users/wadood/projects/VBToCSharp/FlexCore/Spreadsheet/SpreadsheetControl.razor
[fc-sheet-css]: /Users/wadood/projects/VBToCSharp/FlexCore/Spreadsheet/SpreadsheetControl.razor.css
[fc-editor-model]: /Users/wadood/projects/VBToCSharp/FlexCore/Editor/EditorModels.cs:11
[fc-editor-lite-model]: /Users/wadood/projects/VBToCSharp/FlexCore/Editor/EditorLiteModels.cs:6
