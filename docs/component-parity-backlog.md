# Component-level parity backlog for FlexCore

Verified 2026-09-05 against the active FlexCore and FlexKit source trees, the supplied `FlexKit-Telerik-Parity-Audit.html`, and the [current Telerik UI for Blazor catalog](https://www.telerik.com/blazor-ui/documentation/introduction#list-of-components). This is an inventory, not authorization to implement new components. Names below describe component families; proposed FlexCore class names have not been adopted.

No additional active Razor component files were found in the checked FlexKit tree that are absent from FlexCore. FlexCore additionally contains `Grid/GridPagerControl.razor`. Hidden backup directories, build output and test fixtures were excluded. The audit's “Components being built” table is a proposal/build queue, not evidence that those implementations are available to port in either checked repository.

## Newly implemented dedicated UI components

The user authorized the remaining 32 controls. These formerly missing families now have dedicated FlexCore implementations and independent FlexKitTester benches. See [control APIs, benches, verification and remaining feature boundaries](remaining-controls.md).

| Area | Newly implemented components |
| --- | --- |
| Workbook editing | Spreadsheet |
| Scheduling | Scheduler |
| Geographic visualization | Map |
| File transfer | Upload; external DropZone |
| Input | MaskedTextBox; DateRangePicker; DropDownTree; RangeSlider; Signature |
| Color editing | ColorGradient; FlatColorPicker |
| Selection buttons | ButtonGroup; SegmentedControl |
| Compact content | Chip and ChipList; Avatar; FloatingLabel |
| Layout utilities | AnimationContainer; MediaQuery; StackLayout |
| Loading and progress | Loader; LoaderContainer; ChunkProgressBar |
| Anchored overlays | Tooltip; Popover; reusable Popup |
| Conversational UI | Chat; PromptBox; InlineAIPrompt |
| AI input actions | SmartPasteButton; SpeechToTextButton |

Telerik also documents [ChipList](https://www.telerik.com/blazor-ui/documentation/components/chiplist/overview) and [Avatar](https://www.telerik.com/blazor-ui/documentation/components/avatar/overview) separately from the main catalog. Dedicated ChipList and Avatar implementations now exist in FlexCore. Tooltip and Popover also have their own library implementations.

## Dedicated controls implemented from existing foundations

The user subsequently authorized this group. Dedicated counterparts are now implemented; see [their APIs, examples and remaining feature limits](dedicated-controls.md). The table below preserves the original work scope for traceability. It no longer represents missing component types.

| Component family | Existing FlexCore foundation | Work required for the dedicated counterpart |
| --- | --- | --- |
| Gantt | `ChartControl`, `ChartType.Gantt` | Editable task tree/timeline, dependencies, task dragging/resizing, scheduling views and CRUD. A static chart is not an interactive Gantt control. |
| PDF Viewer | `ReportWriterControl` can display host-produced PDF bytes in an iframe | Library-owned paging, zoom, search, selection, open/download/print, annotation and form-filling UI. |
| Form, FormItem, FormGroup, automatic field generation | `PropertyGridControl`, `EditorPanel`, `EditorPanelRow`, existing field controls | Model/EditContext binding, validated submission, per-field metadata/templates, grouping and layout. Could be implemented by extending the existing form foundations. |
| Calendar | Calendar implementation inside `DatePickerControl` | Standalone calendar API, calendar views, selection modes and templates. |
| TimePicker | `DatePickerControl.ShowTime`, `TimeSpanPickerControl` | Clock-time picker with time selection popup and stepping. Duration entry is a different use case. |
| Stepper | `WizardControl` step header | Standalone step navigation, orientation, state and validation indicators. |
| AutoComplete | Editable/filtering mode in `DropDownListControl` | A dedicated suggestion API, minimum input length, debounce, remote reads and templates; reuse the dropdown infrastructure. |
| RadioGroup | Individual `RadioControl<TValue>` instances | Data binding, group layout, validation and group-level keyboard behavior. |
| ToggleButton | `ToolbarButtonControl.Selected`, ribbon buttons | Standalone two-way selected state and toggle semantics. |
| ColorPalette | Fixed palette inside `ColorPickerControl` | Standalone configurable palette data, dimensions and selection. |
| Arc/Circular/Linear/Radial Gauge controls | Gauge modes in `ChartControl` | Public gauge-specific configuration, ranges, pointers, labels and templates. Existing renderers should be reused. |
| StockChart | Candlestick/OHLC/HighLow chart modes | Financial navigator, date-range selection and zoom/pan. |
| DockManager | `MdiHost`, `MdiPane`, fixed panel layouts | Dock/undock, floating panes, pin/unpin, nested pane layout and saved docking state. |
| AppBar | `MenuBarControl`, `ToolbarControl`, `StatusBarControl` | Configurable sections, spacers and static/sticky/fixed positioning. |
| GridLayout | `DashboardLayoutControl`, `PageLayoutControl` | General row/column track definitions, spans, alignment and responsive layout. |
| ValidationSummary, ValidationMessage, ValidationTooltip | Grid validation and standard Blazor validation infrastructure | Reusable, consistently styled FlexCore validation presentation outside the grid. |
| SvgIcon and FontIcon | `IconCss`, image paths, glyphs and grid-specific icon helpers | Reusable icon components and a consistent icon catalog. |
| AIPrompt | `Editor/LlmResultCard` | Prompt input, suggestions, commands, views and output actions; existing result rendering can be reused. |

The Gantt, StockChart and PDF Viewer distinction is supported by the official [Gantt](https://www.telerik.com/blazor-ui/documentation/components/gantt/overview), [StockChart](https://www.telerik.com/blazor-ui/documentation/components/stockchart/overview), and [PDF Viewer](https://www.telerik.com/blazor-ui/documentation/components/pdfviewer/overview) documentation.

## Existing components that need extension, not replacement

The current audit still lists feature gaps in these existing families. They are not missing component types:

- Grid, TreeGrid, Pivot, DataFilter, ListView and ListBox.
- FileBrowser/FileManager behavior and FilePicker/FileSelect behavior.
- DropDownList/ComboBox, MultiSelect and MultiColumnComboBox.
- TextBox, TextArea, NumericTextBox, DateInput, DatePicker and combined DateTime entry.
- CheckBox, Radio, Switch, Slider, Rating and SecurityCode.
- Editor and EditorLite.
- Menu, ContextMenu, Breadcrumb, TreeView, Toolbar, Ribbon and buttons.
- Tabs, Wizard, Sidebar/Drawer, Accordion/PanelBar/ExpansionPanel.
- Dashboard/Tile layout, Splitter, Dialog/Window, Card and Carousel.
- Diagram/Drawing, Charts, gauges, Barcode and QR Code.
- Notification, ProgressBar, Skeleton and Badge.
- ReportWriter, ReportDesigner, report parameter editing and report creation wizard.

For example, `DatePickerControl.ShowTime` already supplies combined date/time entry; another DateTimePicker class is not necessary just to expose that behavior. `GridPagerControl` already exists. Barcode and QR controls now generate actual encoded symbols. Their remaining feature gaps should be addressed in those controls.

Chart additions such as Radar Column, Scatter Line, Horizontal Waterfall and trendlines belong inside ChartControl. They should not inflate the count of missing standalone controls.

## Shared systems and broader product scope

These are separate from the UI component list:

- Shared popup/root infrastructure, localization, RTL, consistent themes, accessibility, adaptive/mobile rendering and automated accessibility checks.
- LLM provider integration, editor/grid AI actions, WebMCP support, and an optional component-documentation MCP server/coding assistant.
- General PDF and Word document processing, large-workbook streaming, and broader report rendering/export. Existing ClosedXML and framework ZIP support should be assessed before adding replacement libraries; a custom ZipControl is not required.
- **MediaPlayer:** absent locally and listed in the audit build queue, but not confirmed as a native Telerik UI for Blazor component. Telerik's [MediaPlayer documentation](https://www.telerik.com/design-system/docs/components/mediaplayer/) lists jQuery, ASP.NET Core and MVC. Treat this as broader product scope, not a verified Blazor parity requirement.
- **ActionSheet:** absent locally; the audit cites adaptive/internal API types. A dedicated public Blazor counterpart was not confirmed. Track mobile action-sheet behavior separately until its exact target is agreed.

## Suggested order if implementation is authorized later

1. Shared Popup/Tooltip/Popover, loading, validation and icon infrastructure.
2. Form, AutoComplete, MaskedTextBox, Calendar/TimePicker/DateRangePicker and Stepper, reusing existing controls.
3. Upload/DropZone, Signature and PDF Viewer.
4. Scheduler, interactive Gantt, Spreadsheet, DockManager and Map.
5. Remaining layout/selection utilities and optional AI experiences.

This is a source-checked component backlog for the supplied audit and current catalog, not a claim of complete API, behavior or accessibility parity. Full parity also requires closing the remaining feature gaps within the existing components.
