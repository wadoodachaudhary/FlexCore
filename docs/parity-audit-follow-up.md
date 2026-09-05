# FlexCore parity audit: verified fixes

Reviewed 2026-09-05 against the supplied `FlexKit-Telerik-Parity-Audit.html` (900 findings across 30 groups). Scope: high-impact defects in existing FlexCore controls and remaining grid/editing gaps. No new production UI components, host-app UI, or FlexKit synchronization.

The audit is a candidate backlog, not proof of missing behavior. It combines several features in individual findings and sometimes describes an older GridControl implementation. The work below does not claim that all 900 findings were verified or that FlexCore has complete Telerik parity.

## Confirmed defects fixed

| Audit reference | Evidence and resulting behavior | Limits |
| --- | --- | --- |
| Barcodes-1 | BarcodeControl previously derived bar widths from character values. It now uses ZXing.Net and renders an encoded SVG with quiet zones. `Type` supports Code128, Code39, Code93, EAN8, EAN13, UPCA, UPCE, ITF and Codabar. `BarHeight` controls the bars. Invalid data/checksums produce a visible error. | Nine formats, not the audit's complete 19-format list. Default Code128. Invalid colors, contrast or undersized output can still make a real symbol unreadable. |
| Barcodes-2 | QrCodeControl previously used a hash-based pattern. It now encodes UTF-8 QR data with standard error correction, adaptive matrix sizing and a four-module quiet zone. `ErrorCorrection` selects Low/Medium/Quartile/High. | SVG output; no logo overlay, camera or image export added. |
| Pivot-1 | Sort state previously changed without being used in BuildPivot. Row and column dimensions now sort by typed values; value-column headers sort sibling rows by aggregates. Header order follows the actual value-column order. `SortDescriptors`, `SortDescriptorsChanged`, `SortByAsync` and `ClearSortingAsync` expose state. | Parent dimensions retain nesting; aggregate sorts order siblings. This is not a replacement pivot engine or full keyboard navigation. |
| Filter builder-1, -2 | DataFilterControl ignored `FilterProperty.Type` and rendered native selectors. It now uses existing FlexCore controls: TextBoxControl for text/numeric drafts, DatePickerControl for dates, DropDownListControl for Boolean/enum values and field/operator choices, and ButtonControl for actions. Invalid numeric drafts remain visible. Operator choices follow field types and nullability. | Numeric drafts use TextBoxControl with typed validation, not a spinner/formatted numeric editor. DateTimeOffset/time values use validated text. |
| Filter builder-3 | Added `FilterEvaluator.BuildPredicate<T>` and `DataFilterControl.BuildPredicate<T>` for safe, typed in-memory filtering. Nested AND/OR groups, comparison/string/null operators, culture, case sensitivity, nullable paths and dictionaries are supported. Invalid values/fields fail instead of silently becoming zero. | This is a snapshot predicate for local data, not an IQueryable/SQL translator or Telerik DataSourceRequest adapter. Rebuild after filter changes. Empty draft conditions/groups are ignored. In/NotIn list syntax is not exposed. |
| Filter builder-4, -5 | A replacement `Value` is adopted after initialization; null resets the editor. Notifications are awaited. Per-field `ValueTemplate` receives the property, condition and `SetValueAsync`. | Same-instance model changes are reflected on the next parent render. Custom editors own their draft/commit behavior. |
| Grid-26 | `EditSettings.ShowConfirmDialog` previously contained only a TODO. Row commands now request confirmation; toolbar deletion confirms the selected batch once. Cancel/Escape leave data unchanged. Confirm honors row-deletion vetoes, updates selection/cache and prevents duplicate submissions. | Local CRUD uses mutable data; provider CRUD remains host-owned. Programmatic `DeleteRecordAsync` retains its existing direct-operation contract. |
| Grid-5, partial | F2 opens the active editable batch cell or the selected inline/dialog row. Existing read-only/primary-key and editing permissions remain enforced. | Other keyboard shortcuts in this broad audit item are not all implemented by this change. |
| Dialogs & windows-3, partial | Existing DialogControl now supplies `role="dialog"`, modal state and a stable title relationship. Callers may provide `AriaLabel` and `AriaDescribedBy`. | Complex dialog bodies are not automatically read as one description. No claim of WCAG/Section 508 certification. |
| Additional runtime defect | TextBoxControl registered a null delegate for keydown when no listener was supplied. Real browser typing exposed a renderer NullReferenceException per keystroke. A default EventCallback now omits that handler; supplied callbacks still dispatch. | Shared by the normal, password and multiline native editor branches. |

The barcode dependency is [ZXing.Net](https://github.com/micjahn/ZXing.Net), version 0.16.11. It encodes matrices in .NET without adding JavaScript or a platform drawing dependency.

## Grid findings that already have implementations

These are **not blanket declarations of complete parity**. They identify why the audit's “missing” or “flat-only” descriptions should not drive duplicate implementations.

| Audit reference | Current source evidence |
| --- | --- |
| Grid-1, -24, -42 | `GridItemsProvider.cs`, `GridControl.ProviderGrouping.cs`: query descriptors, group requests, lazy group children and provider aggregates. Providers still translate their own backend queries. |
| Grid-2, -41, -50 | `GridControl.State.cs`, `GridState.cs`: state capture/apply, grouped state and scroll restoration. Audit-specific Telerik state members are not all identical. |
| Grid-3 | `GridControl.Validation.cs`, `GridControl.RowEditing.cs`: EditContext, DataAnnotations, validation templates and draft editors. |
| Grid-4 | Grid markup and keyboard selection already include grid/row/cell semantics and navigation. Formal accessibility conformance remains a separate task. |
| Grid-6, -7, -21, -38 | `GridControl.PresentationFeatures.cs`, `GridPresentationModels.cs`: empty/loading presentation, cell hooks and highlighting. |
| Grid-9, -10 | Existing toolbar templates, column/context menus and multi-sort dialog. Per the user's request, header ellipsis buttons remain opt-in; standard context-menu multi-sort remains available. |
| Grid-11 through -15 | `GridControl.Filtering.cs`, `GridFilterTemplateContexts.cs`: typed filter rows, operator configuration, checklist menus and filter templates. The filtering bench already includes a Contains-only row option. |
| Grid-18 through -20 | `GridControl.ColumnFeatures.cs`: metadata-driven columns, stacked headers and cumulative frozen column positions. |
| Grid-32 through -34 | Existing pager configuration, GridPagerControl and `GridControl.ColumnVirtualization.cs`. No new pager was added in this work. |
| Grid-49 | Export options and `GridControl.ProviderExport.cs`: export hooks and full-provider-query export with progress/cancellation. Exports still accumulate in memory. |
| Grid-51 | Existing ordered multi-sort descriptors and context-menu sort editor. |

## Remaining work

The broader backlog remains: pivot field filters/templates and full keyboard navigation; additional barcode symbologies; provider-side filter translation; advanced popup form configuration; localization; and other component groups that were not examined deeply in this pass. Missing standalone controls are excluded by the user's instruction not to create new components.

## Verification

The durable regression runner is `tests/FlexCore.RegressionTests` (88 passing checks). It exercises real rendered components and event handlers, including decoding rasterized barcode/QR SVG output, typed/nested filters, replacing filter state, async callbacks, numeric/date/aggregate pivot sorting, grouped header alignment, delete confirmation/cancellation/vetoes and F2 editing.

An isolated browser fixture (19 passing checks) additionally scans actual browser screenshots with an independent zxing-cpp reader, types into the filter builder, opens the FlexCore calendar, keyboard-sorts the pivot, edits with F2/Enter and confirms/cancels selected-row deletion. The earlier real enterprise-bench typing, dialog, validation and checkbox/ID navigation suite also passed all 183 browser assertions against the shared controls. No user application is launched or restarted.
