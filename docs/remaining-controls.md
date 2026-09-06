# FlexCore control benches and new counterparts

All 32 dedicated controls in the verified missing-control table now have FlexCore implementations and **separate FlexKitTester pages**. The tester's **FlexCore Control Benches (32)** navigation item opens `/flexcore-controls`. Each route mounts only its own bench and owns its sample state. The normal tester target uses FlexCore; these pages are excluded from the optional `UseFlexKit=true` build.

The controls use the `Fx.ControlKit` namespace unless a namespace is shown below. The host uses FlexCore controls for interactive UI. HTML/SVG primitives and the browser bridge live in the library.

## Control inventory

All bench paths below are relative to `/flexcore-controls/`.

| Public control | Bench | Implemented behavior |
| --- | --- | --- |
| `PopupControl` | `popup` | Anchored content, open binding and methods, external anchor ID, placement, viewport clamping, outside dismissal, Escape and focus return. |
| `TooltipControl` | `tooltip` | Text or rich content, hover/focus trigger, delay, placement and descriptive accessibility relationship. |
| `PopoverControl` | `popover` | Header, editable content, footer, close action and the shared popup behavior. |
| `ButtonGroupControl` | `button-group` | Single/multiple selection, optional empty selection, disabled items, horizontal/vertical layout and arrow-key focus. |
| `SegmentedControl` | `segmented` | Mutually exclusive bound choices and native radio keyboard semantics, styled as segments. One enabled choice is a Tab entry point; explicit tab indices also make clicked segments receive keyboard focus in Safari. |
| `ChipControl` | `chip` | Selection, removal, icon/content and independent callbacks. |
| `ChipListControl` | `chip-list` | Data-bound chips, single/multiple selection and selection cleanup on removal. |
| `AvatarControl` | `avatar` | Image, initials/content, three shapes, sizing and failed-image fallback. |
| `FloatingLabelControl` | `floating-label` | Associated label, focus/value state and required indicator for a composed field. |
| `StackLayoutControl` | `stack-layout` | Direction, wrapping, gap, item alignment and justification. |
| `AnimationContainerControl` | `animation-container` | Fade/slide/scale, show binding, duration, retained content and inert hidden content; reduced-motion support. |
| `MediaQueryControl` | `media-query` | Live media-query matching, match callback, matched/unmatched fragments and prerender fallback. |
| `LoaderControl` | `loader` | Spinner, pulsing and dots, accessible text, visibility, size and reduced motion. |
| `LoaderContainerControl` | `loader-container` | Busy overlay, optional interaction blocking, custom loader content and existing-content preservation. |
| `ChunkProgressBarControl` | `chunk-progress-bar` | Numeric bounds, chunks, horizontal/vertical layout, determinate/indeterminate states and progress semantics. |
| `MaskedTextBoxControl` | `masked-text-box` | Required/optional digit, letter, alphanumeric and character slots; literals, escapes and case conversion; commit validation, retained invalid draft and EditContext integration. |
| `RangeSliderControl` | `range-slider` | Two keyboard-accessible endpoints, positive step, bounds, non-crossing values, formats and EditContext notification. |
| `DateRangePickerControl` | `date-range-picker` | Two typed date editors, standalone range calendar, date bounds, disabled dates, range errors and EditContext validation. |
| `DropDownTreeControl` | `drop-down-tree` | Hierarchical selection, ancestor-preserving search, clear, disabled/leaf-only choices and asynchronous children callback. |
| `ColorGradientControl` | `color-gradient` | Two-dimensional saturation/brightness surface, full-spectrum hue track, color-aware saturation/brightness/opacity tracks, compact preview over a transparency checkerboard, accessible sliders with rounded degree/percentage labels, hex RGB/RGBA editing and visible invalid-input errors. |
| `FlatColorPickerControl` | `flat-color-picker` | Palette/gradient views and immediate or Apply/Cancel commit mode. |
| `SignatureControl` | `signature` | Mouse/touch/pen strokes, bound vector model, undo/clear, read-only mode and generated SVG download. |
| `UploadControl` | `upload` | Multiple-file queue, extension/size/count bounds, chunks, acknowledged progress, manual/automatic upload, cancellation, retry and removal callbacks. |
| `DropZoneControl` | `drop-zone` | External browser-file drop target linked to an Upload ID; shares the same queue and validation. |
| `AI.PromptBoxControl` | `prompt-box` | Multiline input, Enter/Shift+Enter, attachment metadata, send/cancel and toolbar content. |
| `AI.ChatControl` | `chat` | Bound conversation history, message template, suggestions, provider reply, cancellation, attachments and error display. |
| `AI.InlineAIPromptControl` | `inline-ai-prompt` | Anchored AI prompt with supplied selected-text context, commands, generated output and apply action. |
| `AI.SmartPasteButtonControl` | `smart-paste-button` | Clipboard/manual-paste input, allowlisted field schema, provider mapping, review dialog, typed conversion and rollback on failed application. |
| `AI.SpeechToTextButtonControl` | `speech-to-text-button` | Browser capability detection, language, continuous/interim/final recognition, stop, transcript binding and explicit permission/provider errors. |
| `Spreadsheet.SpreadsheetControl` | `spreadsheet` | Real XLSX workbook, formulas/recalculation, cell/formula edits, keyboard/range selection, formatting, copy/paste, merge, sort, fill-down, structural edits, sheets, undo/redo, protection and download. |
| `Scheduling.SchedulerControl` | `scheduler` | Day/week/work-week/multi-day/month/timeline/agenda views; resource grouping, overlaps, CRUD, recurring occurrence/series editing, drag move/resize, timezone conversion, async reads and veto callbacks. |
| `Maps.MapControl` | `map` | Web Mercator projection, optional raster tile template, polygon/line layers, markers/bubbles/clusters, pan/zoom, fitting, layer toggles, templates and selection callbacks. |

## Host setup

Load the host's generated `{ApplicationAssembly}.styles.css`; it includes the FlexCore scoped styles. Continue loading `_content/FlexCore/fx-grid-core.css` for shared grid styling. No additional service registration is needed for these controls. Dialog-based controls require an interactive Blazor render mode, as the tester pages demonstrate.

```razor
<PopupControl @bind-IsOpen="open" AriaLabel="Choose an option">
    <Anchor Context="popup">
        <ButtonControl OnClick="() => popup.ToggleAsync()">Options</ButtonControl>
    </Anchor>
    <ChildContent>Any FlexCore content</ChildContent>
</PopupControl>

<RangeSliderControl @bind-Value="range" Min="0" Max="100" Step="5" />
<MaskedTextBoxControl @bind-Value="phone" Mask="(000) 000-0000" />
<DropZoneControl UploadId="documents" />
<UploadControl Id="documents" UploadHandler="ReceiveChunkAsync"
               AllowedExtensions="@(new[] { ".pdf", ".txt" })" />
```

`ChoiceSelectionMode` is used by ButtonGroup/ChipList. It is deliberately distinct from the existing grid's `SelectionMode`, so importing both control namespaces remains compatible.

### Input contracts

Masks format and validate on Enter or blur. Live typing retains the draft, including an invalid draft; it never silently discards characters. `MaskPattern.Apply()` can be used separately by a host. `IncludeLiterals=false` publishes only entered characters. The mask characters are `0`/`9` (required/optional digit), `L`/`?` (letter), `A`/`a` (alphanumeric), `&`/`C` (character), `#` (digit/sign/space), `>`/`<`/`|` (case mode) and backslash (literal escape).

RangeSlider uses `NumericRange(Start, End)`; DateRangePicker uses `CalendarRange(Start, End)`. DateRangePicker publishes the editable range even when reversed, while its validation message prevents a containing EditContext from submitting. Supply `ValueExpression` when integrating the new input wrappers into a form. RangeSlider offers two labeled sliders instead of an overlapping-thumb hit target.

DropDownTree copies the supplied nodes to keep expansion/selection changes out of caller-owned objects. IDs must be unique and acyclic. `LoadChildren(node, token)` is called for branch expansion; asynchronous providers must mark expandable branches with child data. Filtering retains matching descendants and their ancestors.

### File transfer and signatures

`UploadHandler(UploadChunk, CancellationToken)` is required to actually receive bytes. A chunk includes stable file ID, name/type, total size, offset, bytes and final-chunk flag. Await consuming/writing `Bytes` before returning; the buffer is reused. Progress advances **after** the handler acknowledges a chunk. Retry starts from offset zero using the same file ID, so receivers should reset the partial destination on offset zero. A zero-byte file produces one empty final chunk. `RemoveHandler` can remove a previously uploaded file from the host's destination.

No URL or server storage path is hardcoded. The Upload bench stores received bytes only in its component's memory. Application storage and server validation remain host responsibilities. The file picker inputs for earlier selections remain alive while their queued files need the browser streams.

Signature values contain strokes and points, not arbitrary imported markup. `ExportSvg()` returns a complete generated image with a view box. No signature-recognition or biometric-identification service is involved.

### Spreadsheet

`SpreadsheetDocument` owns an existing ClosedXML workbook engine. `SetCell`, `SetRange`, `Format`, `FillDown`, `Merge`, `Sort`, structural-edit methods, `Undo`, `Redo` and `Save` also work without the UI. The UI accepts `DocumentBytes` and exposes `Document`, `Selection`, `Changed`, `Saved` and `SaveAsync()`.

XLSX import is capped at 50 MiB compressed / 256 MiB expanded. The UI renders a bounded row/column viewport with navigation; hosts configure `RowCount`, `ColumnCount`, `VisibleRows` and `VisibleColumns`. Formula evaluation uses the functions supported by ClosedXML. Errors remain visible. Protected cells cannot be changed through the editing API, and failed multi-cell mutations restore the previous workbook.

### Scheduler

Appointments are `SchedulerEvent` models with stable IDs, local `Start`/`End`, `TimeZoneId`, optional `ResourceId`, `RecurrenceRule`, exclusion dates and optional `SeriesId`. End is exclusive. `SchedulerEngine.Expand()` resolves RFC 5545 rules via **iCal.NET 5.2.3** and keeps recurring local times stable across daylight-saving transitions. Invalid dates/timezone IDs and local times in daylight-saving gaps are rejected.

Edits publish cloned events through `EventsChanged`, after the asynchronous `Changing` callback can veto. Editing a single occurrence excludes its original start from the master and adds an independent replacement linked by `SeriesId`. Deleting a series removes its replacements too. `OnRead(range, token)` supports host data loading with cancellation. Resources and timezone IDs are supplied by the host.

### Map and AI

Map requires no vendor SDK. A `TileUrlTemplate` is optional; `{z}`, `{x}` and `{y}` are replaced for the tile requests. Hosts provide the service URL, any required access configuration and attribution. The default bench draws local geographic vectors and makes no tile-provider requests. `MapProjection.ReadGeoJson()` parses polygon/multipolygon/line/multiline/geometry-collection data into shape layers. Point data is supplied through `MapMarker` models.

Chat, InlineAIPrompt and SmartPaste require host callbacks to perform actual AI work. Their demos explicitly label local responses/parsing. SmartPaste sends only the allowlisted field schema, then requires review before applying the typed values. No credentials or AI endpoint are embedded. Speech uses the browser's recognition service and surfaces unsupported/denied/network cases; the host can always provide an editable transcription field.

## Browser boundary

`wwwroot/browser-capabilities.js` is one lazily imported module with one exported dispatcher. It provides the DOM capabilities that C#/CSS do not expose: popup measurement, `matchMedia` events, external `FileList` delivery, clipboard/download, signature pointer capture and speech recognition. Each observer/listener/recognizer is detached on component disposal. Popup CSS placement, manual paste/file selection, and host model APIs remain available when the corresponding browser capability is unavailable.

## Parity boundaries still requiring feature work

These implementations close the **32 missing dedicated component types**. This is not a claim of identical Telerik APIs, complete accessibility conformance, or every feature of the proprietary controls.

- Spreadsheet renders cells, formulas and supported styles. Embedded worksheet images/charts, hyperlink editing tools, conditional-formatting/data-validation authoring, advanced ribbon tools and visual frozen-pane scrolling are not yet implemented. Freeze settings are saved to XLSX. Imported unsupported workbook content is handled by ClosedXML, rather than recreated by the UI.
- Scheduler timeline currently groups day cells by resource; it is not a continuous horizontal timescale. Recurrence has presets plus an RRULE field; a full recurrence-rule visual builder and custom appointment/cell templates remain work.
- Map shape import covers geographic paths; point features use marker models. Cross-antimeridian geometry splitting, service-specific geocoding and accessibility certification are not provided.
- Signature exports vector SVG. PNG/JPEG import/export, signing cryptography and advanced signature processing are separate capabilities.
- Upload provides transport callbacks, not a built-in HTTP endpoint or resumable-upload protocol. Speech support varies with browser/provider and needs a real microphone check on the user's browser.
- Theme-wide RTL/localization/adaptive behavior and the existing controls' feature gaps from the original audit still need individual verification. MediaPlayer and ActionSheet remain the separately identified broader-catalog items, outside the 32 confirmed Blazor component gaps.

## Verification

```sh
# FlexCore
 dotnet run --project tests/FlexCore.RegressionTests
 cd tests/FlexCore.RemainingControls.BrowserTests
 dotnet run
```

Validated with 168 C# checks, 70 new-control browser checks, the prior 29 counterpart checks and 202 grid/parity checks. FlexKitTester and FlexCore.Showcase build successfully.

The browser fixture requires Python Playwright/Chromium and openpyxl, and links the **real FlexKitTester bench pages**. It starts an isolated ephemeral loopback fixture and shuts it down when checks complete. It does not start or restart the user's applications. `FLEXCORE_REMAINING_ONLY=spreadsheet,scheduler` selects specific benches during development. Speech event delivery is simulated in automation; no recorded audio or microphone input is sent to a service.

Reference behavior: [Telerik Spreadsheet](https://www.telerik.com/blazor-ui/documentation/components/spreadsheet/overview), [Scheduler](https://www.telerik.com/blazor-ui/documentation/components/scheduler/overview), [Map](https://www.telerik.com/blazor-ui/documentation/components/map/overview). Recurrence engine: [iCal.NET](https://github.com/ical-org/ical.net), [pinned NuGet version](https://www.nuget.org/packages/Ical.Net/5.2.3).
