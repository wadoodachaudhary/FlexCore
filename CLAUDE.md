# FlexCore Work Notes

## 2026-09-25 R2-QA Grid and Dropdown Batch

- HHM-1156 reverified against FlexKit: 220 vendor-assignment browser checks and 572 visual/geometry checks pass. No owner application restart; test fixtures stopped.
- Mirrored FlexKit changes for HHM-901 (size-independent classic checkbox marks), HHM-1157 (`ColumnRenameMaxLength`, default 255, enforced in browser and before persistence), HHM-1170 (remove stale same-row keyboard cell cues), and HHM-1132 (page dropdown arrival positioning/reveal without an extra server round trip). Hosted editors keep their existing guarded focus path. No new production JS files or dependencies.
- FlexKitTester `/r2qa-grid` and `verification/r2qa-*.mjs` provide synthetic 129-column tests with 300-500 ms added RTT. Stale double-highlight frames fell from 41 to zero; Enter, Tab and click-away batch commits pass. Five deferred-scroll buffer rows reduce rendered cells from 8,127 to 5,547. Column virtualization failed navigation checks and was not enabled in HomeFront.
- Active HomeFront also opts read-only worksheet cells into drag selection and retains picker dates as DateTime with separate display formatting. HHM-1007 multi-sort is correct: later levels break ties in earlier levels. Database layouts and frozen HomeFrontPB remain untouched.
- Required nonincremental builds pass: active HomeFront 208 warnings/zero errors; Showcase zero warnings/errors. Database-free picker/rename/sort/wizard/estimate suites pass 1,981 checks. Full dropdown browser checks pass including 600 ms added RTT, editable focus, upward opening and native grid editors. The separate database-dependent pricing suite failed its SQL-success assertion. Remote-QA scroll timing and Jira dry-run approval remain outstanding.

## 2026-09-25 Blocked Crystal reports (JavaToCSharp/Reports/blocked-52)

- All 52 RPTs convert; 51 paginate with deterministic synthetic data. 0248 stops with PageAreaTooLarge, as Crystal's SinglePageFormatter does when its 24-row page-header subreport is taller than the page. Of the 530 distinct corpus RPTs, 527 reach pagination (472 before). None of this is Crystal visual approval.
- Formula engine (`CrystalFormula*.cs`): a hand-written, bounded parser for Crystal and Basic syntax replaces Sprache (removed from FlexCore.csproj). Adds subscripts and string ranges, Select/Case, arrays (Redim/Redim Preserve, literals, Count/Sum/Maximum... over arrays), While/Do/For loops (100,000 iterations, 2,000,000 steps per evaluation), date-range functions (YearToDate, LastYearYTD, ...), DateSerial/TimeSerial and the other built-ins the corpus uses. `=` against a range or array is membership, `Not` binds tighter than `=`, and `Local` is reserved.
- Custom functions: the converter writes `<CustomFunctions>`; the loader compiles each function separately (`CrystalCustomFunctionLibrary.Errors` names unusable or recursive ones) and passes them to every compile site of that report: field formulas, record/group selection, condition formulas, running totals, group-name, specified-group and Top N formulas. Subreports get their own.
- Engine: GroupNumber, GroupName, GroupingLevel, HierarchicalChildren, ConditionalAggregate and DocumentProperty come from the layout session. Adds hierarchical grouping, date/time group conditions, specified-order groups (Others merged, discarded or kept), group sort by summary with Top/Bottom N and ties, page-header/footer subreports, and subreport links from parameters and formulas. Refused with a precise NotSupported: Boolean group conditions, percentage Top N, and summary sorts on hierarchical groups.
- Converter: all 12 saved SectionProperties values are decoded (Keep Together and Suppress match the SAP exports on 294/226 sections), plus group options, summary information and parameter value types. Converted XML carries `FlexKitXmlVersion` (`CrystalReportXmlVersion.Current`). Unstamped XML from the earlier converter still loads: placeholder group sorts are skipped and missing group conditions inferred, each with a LAYOUTDIAG. Hosts should convert such imports again from their kept RPT.
- The loader ignores page breaks before the report header and after the report footer (Crystal never breaks there), and the writer no longer special-cases host subreport names. Installed Crystal XML exports whose subreport report headers carry New Page Before paginate differently as a result.
- The converter reads ReportKind (record 102) and MultiColumnInfo (record 108) and writes `<DetailAreaFormat EnableMultipleColumnFormatting=...>` for mailing-label and multiple-column reports; `ReportLayoutColumns` lays the details (and group areas when "Format Groups with Multiple Columns") in columns as Crystal's ReportColumnFormatter does: column count from width + horizontal gap, vertical gap after each area (a group header repeated on a new page included), down-then-across or across-then-down, fixed label height when groups print across the page. Printing at the bottom or a page break after ends the column (row), never the page; a section printed at the bottom goes alone to the column's bottom. Inline subreports stay one column (diagnostic).
- A field object's trailing field identity (index/type after its reference, four more bytes before them in older files) only refines a special field; it no longer rebinds group summaries to database fields (with-subreport fixture, Sales By Customer_grouped).
- Recursive custom functions: each cycle member names a real call chain of its own, cut where the paths meet, in constant work per member.
- Known gaps, not blockers: legacy charts and cross-tabs print placeholders; field number/date formats are not converted; RTF/HTML text interpretation prints the markup.
- Mirrored from FlexKit (the Reports trees are byte-identical). Tests: `JavaToCSharp/tools/ReportDesigner.CrystalSemanticsTests` (new) plus the existing report suites.

## 2026-09-23 Report 1 Native Cross-tab

- Mirrored FlexKit's grid-contained field reader (161 -> 159 -> 158) for `01 Cross Tab Page Numbers A.rpt`: customer rows, shipping-method columns, one Count(Order ID) measure from four cell templates. Source bytes remain preserved; unsupported bindings retain precise diagnostics.
- Mirrored legacy `Group #1 Name` resolution, stable unnamed analytical identity, and unnamed designer-object ID collision fix. Source names/XML stay intact through save/preview.
- The offline sample tool refreshes one report in a transaction. Report #1 now has its shipping dimension and 24 in-range Order IDs in the tester's installed SQLite pack; other catalog entries and datasets remain unchanged. No production JavaScript, Java/IKVM/SAP runtime, reference-XML fallback, HomeFront source edit, or running app change.
- Reproduction and remaining blockers: `/Users/wadood/projects/JavaToCSharp/Reports/BLOCKED-REPORTS-HANDOFF.md`. Analytical styling/total visibility remain explicit editable defaults; original Crystal visual approval is still pending.
- Verified: 23 focused, 107 bench and 330 analytical/matrix checks on each library. Shared changed sources match. Required nonincremental HomeFront (208 warnings/0 errors) and Showcase (0 warnings/errors) builds pass. No running website was changed.

## 2026-09-23 Crystal Corpus Execution Bench

- FlexKitTester `/crystal-reports` now browses the deduplicated downloaded/primary RPT corpus, searches and filters it, navigates previous/next, and converts fresh binaries for the existing designer and viewer. All interactive widgets are shared FlexKit/FlexCore controls; no new production JavaScript.
- SQLite sample datasets are generated offline by `JavaToCSharp/tools/CrystalSamples.Seed`. The host reads the pack read-only and matches binary plus schema fingerprints, including linked subreports. Synthetic projected rows do not certify original SQL joins/data-source behavior or Crystal visual parity. Filters/formulas remain enabled; conversion never consults reference XML.
- Mirrored from FlexKit: native `StartsWith`, `ReplicateString`, and unprefixed Crystal color constants. Remaining formula, analytical-region, mutable scheduling, data coverage and visual-approval gaps are shown separately in the bench and recorded in the corpus audit.

## 2026-09-23 Reports Manage and private conversion

- VB6 FMain.frm:2810–2833 enumerates the reports folder; :3097–3100 opens it in Explorer. Reports → Manage now navigates to the existing Crystal page, with a file/folder selection step and an explicit Convert action. Installed reports remain available; each imported RPT and converted XML lives under `{ContentRoot}/User/Reports/{SHA256(normalized LoginID)}/{import-id}/`, beside per-user preference JSON storage and outside wwwroot. Original names remain in metadata; duplicate names receive distinct import IDs. Failed conversions retain their originals, show their error and can be retried. Native diagnostics are shown with successful XML output.
- Private imports use opaque `user-report:` routes checked against the current session in the viewer/designer. Anonymous imports and raw routes into User storage are rejected. Delete removes only the selected private import after confirmation. Installed report XML is not overwritten or deleted by this manager. Designer saves retain the existing authored-copy behavior.
- FlexKit `FilePickerControl.SelectDirectory` enables native browser folder upload without new JavaScript. Disabled pickers retain their InputFile element so in-flight reads remain valid. Mirrored to FlexCore. Folder-relative hierarchy is flattened into separate import folders; original filenames and bytes are retained. Browser folder-picker support is required; multiple-file selection remains available.
- Regression harness: active HomeFront `verification/ReportManagerChecks` exercises persistent native conversion, subreports, warnings, duplicate names, failed batches, retries, size limits, partial-upload cleanup, private routes, user separation, page callbacks and picker attributes. No app server, frozen HomeFrontPB, database or deployment is involved.
- Validation: 59 report-manager checks pass, including actual component HTML rendering. Non-incremental HomeFront.sln passes with 189 existing warnings/0 errors; FlexCore.Showcase.sln passes with 0 warnings/0 errors. The folder picker sources are identical. Live browser uploads and database-backed previews remain for user testing; no app was started. Limits: 1,000 RPTs per batch, 64 MiB per file, 10,000 total entries per selected folder.

Follow `/Users/wadood/projects/VBToCSharp/AGENTS.md`. Shared code is mirrored from
FlexKit; preserve project-specific files and build FlexCore.Showcase after edits.

## 2026-09-23 Native RPT Legacy Recovery

- Independent strict FlexCore corpus run passes all 550 binaries; every generated XML and audited feature set matches FlexKit exactly.
- Mirrored FlexKit's native converter fixes: physical CFB directory slots, v3 size DWORDs, bounded stream reads without unused padding, pre-v9 headerless archives and Database (TLV), qualified fields/index-based joins, 16-bit parameter-value lengths, embedded report discovery, implicit schemas and decrypt/inflate retries.
- Strict FlexKit audit recovers all 284 prior failures: 511/511 downloaded binaries and 550/550 total binaries emit XML; previously successful feature sets are unchanged apart from diagnostics. NativeFormatTests passes 63 checks and RegionTests passes 226 checks against each library.
- XML-invalid source text is explicitly escaped with original UTF-16 metadata retained. Partial extraction is diagnosed. This is not full rendering parity: unsupported analytical variants/style defaults, one non-equality legacy link and one escaped financial-report formula name remain review items.
- Active HomeFront and FlexCore.Showcase non-incremental builds pass (189 existing HomeFront warnings; no Showcase warnings). No Java/IKVM/SAP runtime, new dependency, database change, source-report edit or running server. Details: `/Users/wadood/projects/JavaToCSharp/converted/docs/native-rpt-recovery-2026-09-23.md`.

## 2026-09-23 Wizard Last Step And Review-Only Grids

- `WizardControl`: the default footer leaves Next out on the last step (`ShowNext && !StepContext.IsLast`); Next there could never be enabled. `WizardNavigationContext.IsLast` lets a custom `FooterContent` do the same. Owner rule: a wizard's last page has no Next at all, not a disabled one.
- `GridControl.ShowActiveCell` (default true; VSFlexGrid FocusRect=flexFocusNone): false emits `data-fx-active-cell="hidden"` on the grid root, the scoped CSS makes `--fx-grid-active-cell-border` transparent there (every cursor ring, the SingleCell-batch ring included, reads it), and the browser arrow-key preview is off so navigation takes the server path. `_activeCell` and the `fx-cell-active` class stay as the keyboard origin and scroll anchor. Review-only recipe: `AllowSelection=false HighlightSelectedRows=false EnableHover=false ShowActiveCell=false`. Never infer it from AllowSelection=false (tick-to-pick grids keep their cursor). A host that flips it live re-creates the grid with `@key`. Still visible by design: the "..." button on ShowEditButton columns, the editing ring, the orange type-search match.
- Default grids render unchanged (the attribute is null). No new JavaScript. HomeFront.sln and FlexCore.Showcase.sln build with --no-incremental; HomeFront's 25 database-free harnesses pass; verified live on the TBD review step (click, ArrowRight: no ring, no inline cue, no selected classes).

## 2026-09-22 Filter Popup Condition Checklist

- The text-condition drafts now narrow the checkbox candidates and selection summary as well as the grid. The lower Search values box further narrows that set; Select All acts only on the visible matches. Checked membership never hides a candidate, and the complete distinct-value set is retained for commits and provider completeness checks.
- Uses the grid's existing display-aware/case-sensitive operator matching, including blank conditions and advanced AND/OR. Both libraries are mirrored; regression coverage is in active HomeFront's database-free GridFilterPopupChecks. No host filter workaround or new production JavaScript.
- Verified 435 .NET checks against each library, 109 offline Chrome checks, and screenshots at 1440px/390px. Chrome uses real rendered markup/assets with simulated .NET acknowledgements, not a live HomeFront session. Nonincremental HomeFront and FlexCore.Showcase builds passed (HomeFront: 189 warnings, zero errors; Showcase: zero warnings/errors). No app server started. The browser fixture now gives its simulated host dialog the role required by the existing keyboard ownership guard.

## 2026-09-23 Ship-Review Fix (filter checklist search)

- The column filter menu now narrows its value checklist to the typed condition. Clearing the
  **Search values** box selects the COMPLETE distinct set again, not the condition-narrowed view:
  selecting only the condition's matches left the commit short of the distinct count, so Apply
  stored a checked-value filter that kept filtering the column after the condition was cleared
  (`SelectFilterChecklistSearchMatches`). GridFilterPopupChecks pins both halves (437 checks).

## 2026-09-21 Ship-Review Fixes (Choose Columns, Best Fit to Grid)

- `DialogControl` with `CloseOnOverlayClick="true"` closes on a backdrop click only when the press and the release were both on the backdrop. A press on a dialog button that slides off onto the backdrop (or the reverse) lands its click on the overlay and used to discard the dialog's edits, e.g. the Choose Columns list. The press/release handlers are wired only when `CloseOnOverlayClick` is true (GridControl's own dialogs); every other dialog renders exactly as before. Only a primary-button press made while the system menu is closed arms a backdrop close, and each backdrop press clears a stale drag-release suppression.
- `SplitterControl` server fallback (JS preview not yet registered): a mouse-move that reports the button already up ends the drag at that position, as the JS preview does, so a release the overlay never saw cannot leave the overlay capturing the next click and committing a stray size. SplitterChecks covers it (32 checks).
- `measureGridAvailableWidth`: a FitColumns GridControl (`fx-grid-width-fit-columns`, scroll surface `width: fit-content`) measures against the host again, so "Best Fit to Grid" grows the columns to the pane instead of reading back the current column total. TreeGrid and other grids keep the scroll-surface measure.
- Verified in Chrome on both libraries: FlexKitTester `/choose-columns-popup` slide-off in both directions keeps the chooser open with its edits (fails on the previous DialogControl), a clean backdrop click still cancels, the 336-check chooser bench passes; `/edit-model-options` Best Fit to Grid grows 896 to 2014 px in a 2042 px pane and is stable on repeat; InputDialogBrowserChecks 300 pass.

## 2026-09-21 Choose Columns Popup Isolation

- Caption follow-up: mirrored chooser-scoped bold black title styling from FlexKit. The existing title remains a regular span; other dialogs, dragging and layout handling are unchanged. Browser checks cover caption color/weight and non-editable, non-disabled markup.
- Caption verification: 352 Chrome chooser checks pass against FlexKit, including desktop/narrow screenshots and 150 ms each-way latency. Mirrored stylesheet matches exactly; required non-incremental active HomeFront and FlexCore.Showcase builds pass. Temporary bench stopped.
- Mirrored FlexKit's draggable `DialogControl` shell for Choose Columns, with local context-menu/mouse/key boundaries. Right-clicks inside the popup or on its backdrop do not reach the grid or host. Column schemas, editing and layout persistence are unchanged.
- The chooser opts out of grid-native navigation and handles immediate Escape before dialog interop is ready. The existing dialog key listener ignores nested dialogs so their Escape/Tab do not operate on the parent.
- Database-free browser bench: `HomeFront/FlexKitTester/choose-columns-popup`, with `verification/choose-columns-popup.mjs`; desktop/narrow windows, 150 ms each-way latency, dragging, host event counters, column actions and nested-dialog checks. No database or frozen HomeFrontPB work.
- Verification: 336 Chrome chooser checks pass against each library; FlexKit's existing prompt suite passes 300 lifecycle/Escape/focus checks. Final non-incremental active HomeFront and FlexCore.Showcase builds pass; all temporary test servers stopped.

## 2026-09-21 Keyless Grid Editor Events (HHM-1134)

- Mirrored FlexKit's one-line null-safe Key length check in `IsEditorOwnedTypingKey`; no editing, navigation or buffering flow changes. The keyless event previously threw in the TextBox-to-Grid callback.
- FlexKit's active-host fixture passes 193 component assertions and 120 Chrome layout checks. Required non-incremental HomeFront and FlexCore.Showcase builds pass. No live QA-database retest or frozen HomeFrontPB build.

## 2026-09-20 Advanced Native Variants And Nested Cells

- Mirrored FlexKit's deferred native analytical binding resolution, each-record charts, stacked/percentage bars, donut, DistinctCount/Median and typed ordering. Unsupported native semantics and negative stacking retain explicit diagnostics.
- Shared recursive cell authoring now supports body-row/column merges and nested Table/CrossTab regions with scoped stable-order data, independent growth, nested repeating headers, header-chain boundary protection, vertical/horizontal continuation, preservation and search. Reports and PivotControl match FlexKit; no host-specific UI, package, production JS or runtime dependency added.
- Both libraries pass 1,812 standard checks, including 226 region checks (104 added here), plus 223 partly overlapping parity checks. Five-layout Chrome desktop/mobile geometry checks pass for both. Broad audit remains 17/19 for the prior aggregate-label and unconfigured legacy-route probes; parameter defects are unchanged.
- Final required non-incremental builds with --no-restore pass: FlexCore.Showcase has zero warnings/errors; active HomeFront has 189 existing warnings/zero errors. No host source or database changed and no website was started. Final logs: /tmp/flex-advanced-verification-final, /tmp/flex-advanced-homefront-build.log and /tmp/flex-advanced-showcase-build.log.
- Free-form merged header templates, dynamic row-group cell templates, broader native OLAP/calculated variants and original Crystal visual approval remain open. Resource bounds remain explicit. Details: /Users/wadood/projects/JavaToCSharp/converted/docs/crystal-advanced-regions-2026-09-20.md.

## 2026-09-20 Native Analytical Bindings And Data Regions

- Mirrored FlexKit's bounded native chart/cross-tab binding import, retained source/default diagnostics and containing-group scope. Added Table insertion/shared column editing and pivot-backed vertical/horizontal matrix continuation with repeated labels/headers, full-scope totals, nested widths and wrapped-cell search.
- Reports and PivotControl match FlexKit. Both libraries pass 1,708 standard checks plus 223 partly overlapping isolated checks; 122 new region checks cover conversion-to-viewer, editing and multi-page regions. Desktop/mobile Chrome geometry checks pass. Broad audit remains 17/19 for the pre-existing aggregate-label and unconfigured legacy-route probes; parameter issues are unchanged.
- Required non-incremental FlexCore.Showcase build passes without warnings/errors; active HomeFront build passes with 205 warnings/zero errors. No host source, database, package, production JS or runtime dependency changed. No website started. This is not unrestricted Tablix or complete Crystal visual parity; implementation/limits: /Users/wadood/projects/JavaToCSharp/converted/docs/crystal-analytical-regions-2026-09-20.md.

## 2026-09-18 Opt-In Header Column Checklist (HHM-1099)

- Mirrored FlexKit's `HeaderClickShowsColumns`, default false: opens the checked column list on left- and right-click instead of sorting. `HeaderContextMenuShowsColumns` remains the right-click-only option. Header-menu enablement is respected, hidden captioned columns remain restorable, and captionless structural columns are excluded from the compact checklist.
- Host Attachments keeps Folder grouping internal and uses the right-click column list (left-click sorts, 2026-09-21), removing its duplicate Name header/menu entry. No host-specific rules were added to GridControl; no shared editing, selection, grouping, or persistence flow changed.
- Active HomeFront's database-free `verification/AttachmentColumnChecks` passes 51 Chrome checks (2026-09-21, with the left-click sort check) against the actual Attachments component. The 70 sorting checks also pass. Shared GridControl sources are identical. Required non-incremental HomeFront (203 warnings, zero errors) and FlexCore.Showcase (zero warnings/errors) builds pass.

## 2026-09-18 Single-Column Header Sorting (HHM-996)

- Mirrored FlexKit's fix: ordinary header clicks and ascending/descending column-menu commands replace earlier sorts and their arrows regardless of `AllowMultiSorting`. Multiple levels remain available through the explicit Custom Sort dialog. Sorting comparison, event cancellation and the existing header direction cycle are unchanged.
- `HomeFront/verification/GridSortChecks` passes 70 component checks against each library, including row order, arrows, cancellation, menu commands, restored state, custom sort, pinned blank row and provider reloads. Chrome passes header/menu/custom-dialog interaction and queued clicks at 0/150/300 ms each-way latency against FlexKit; the shared source is identical.
- Required non-incremental HomeFront build passed (203 warnings, no errors); FlexCore.Showcase passed without warnings or errors. No host runtime was restarted.

## 2026-09-18 Ship-Review Fixes To The Grid Filter And Dropdown Work (Update Repositories)

Review wf_47238abd-a8a of the grid-filter (ClientBuffered / SearchAsYouType) and HHM-1025 dropdown change sets:
- ClientBuffered TextBoxControl publishes a value the browser writes while the box is NOT focused (autofill, a password manager filling a second field) as a value-only Sync one tick later; a commit in flight makes it a no-op. Once the listener owns the commit keys the native change is not published, so FLogin's autofilled Password stayed "" (login failed with a saved credential). Focused typing, Tab, Enter and Escape are unchanged. Hosted cell editors never take it (FlexKit's appendTypedText writes relayed keys before the host focuses the editor; Escape must still cancel); a Sync during a pending commit is re-sent afterwards only if the text changed, and a value already sent is not re-sent. A keydown without a key (Chrome's synthetic autofill keydown) is ignored. GridFilterPopupChecks browser.mjs scenario (vi); verified by wf_4c5dd264-729.
- Dropdown one-step open: a current item outside the visible page opens as the TOP row (VB6 combo top index), not the bottom row; an item already visible does not scroll (VB6 would still make it the top row — not changed). A null prepareDropdown result reveals the list the plain way instead of leaving it open and unseen. DropDownOpeningChecks asserts the top-row position and has a README.
- Filter menu Min / Max number boxes (`data-fx-native-vertical-keys`) keep the browser's Up / Down stepping; the listener no longer commits and prevents those keys there.
- Filter row: a debounced commit leaves the queue before it runs, so Enter / Tab / leaving the box during a slow Filtering handler no longer applies the same text twice.
- Known, not changed: the toolbar and side-panel search hosts stop Blazor keydown propagation, so page-level Blazor @onkeydown shortcuts do not fire while those boxes have focus (JS document shortcuts are unaffected; in HomeFront only Components/Pages/DataGrid.razor shows the toolbar search).

## 2026-09-18 Dropdown Opening (HHM-1025)

- Mirrored FlexKit's `DropDownListControl` and existing dropdown JS module: non-hosted lists measure, park, focus and reveal in one browser operation, avoiding intermediate server render/focus round trips. Hosted editors keep their guarded focus path; DropDownGridControl's measurement API is unchanged.
- Keyed openings and generation checks reject stale interop responses. No GridControl flow or logic was changed by this fix.
- Chrome/Blazor Server checks in active HomeFront's `verification/DropDownOpeningChecks` cover 0/150/300 ms each-way latency, close/reopen, selected-value parking, keyboard selection, editable focus, empty lists, upward clipping and native grid dropdown compatibility. The mirrored source files are identical; required HomeFront and FlexCore.Showcase non-incremental builds passed.

## 2026-09-18 Ship-Review Fixes To The 09-17 Report Work (Update Repositories)

Review wf_14845c13-5fc (10 agents) of the 09-17 report/chart change set, then fixes verified by wf_69e310d1-677:
- Viewer page-navigation and search buttons, and the parameter dialog's add button, carry glyph/text content (⏮ ◀ ▶ ⏭ 🔎 ▲ ▼ ✕, `>`) instead of `bi`/`fa` icon classes: hosts such as HomeFront load no icon font, so they rendered blank. The designer's pre-existing `bi` toolbar icons are unchanged.
- Group footers format by column (Currency C2, Integer N0, other N2, Count N0, Average N2) instead of the loader's placeholder C2, and print the aggregate label only when one column has several aggregates. A host-set Format is kept. The loader now gives group Count aggregates N0, as the grand totals already did. A Percent aggregate renders blank instead of failing the render.
- Optional pick-list parameters keep a `...` choice (sentinel key `fx-param-no-value`, mapped to "") so a picked value can be cleared; the multi-value lists are unchanged.
- The Crystal PDF viewer's decoded bytes are cached per PdfDataUrl (a new byte[] each render reloaded it to page 1) and dropped when the PDF view closes.
- Chart bars are clamped to the plot (YAxis.Min > 0 drew them through the axis). Nested-session diagnostics a parent already copied with its element prefix are no longer listed twice.
- Left as recorded behaviour, for the owner: .rpt paths always convert fresh (HomeFront's Reports menu no longer renders the checked-in xml/ exports); FlexCore NuGet users need FlexCore.Documents (unpublished) for the Crystal PDF preview; ReportVectorShape's positional constructor gained a parameter (binary break vs 0.2.44); the new analysis editor uses `bi` icons like the designer.

## 2026-09-17 Report Audit Remediation And Analytical Items

- Fixed declared group aggregates, non-null text Count, first-column totals, encoded aggregate labels, constrained colors, and stable parent-aware group anchors. Explicit SQL/parameter-value logging is replaced by query identity and parameter names/types.
- Every viewer RPT open now converts into an owned temporary workspace; only explicitly opened XML is reused. No sibling/reference XML substitutes for conversion. In-flight conversions clean up if their viewer is disposed.
- Reports' direct input/select/textarea/button/dialog/details/iframe/InputFile sites are replaced with existing shared controls; a lexical regression gate rejects reintroduction. Legacy formatted HTML/tabular rendering, custom rulers and existing business-click JavaScript are not fully removed. Host-specific raw controls remain separate work.
- Added typed analytical bindings/editor, real shared ChartControl/PivotControl children, finalized positioned page snapshots, and same-snapshot HTML export with shared chart/pivot styles. Bar/line/pie, null-aware Sum/Count/Average/Min/Max, report/group scope and fixed-rectangle cross-tabs are supported. Negative pies and unsupported mutable/page aggregate bindings are explicit diagnostics. Native RPT analytical bindings and automatic matrix pagination are still missing.
- Viewer search now indexes finalized output in managed AngleSharp 1.8.1: matches, navigation, highlights, clear and bounds. No production JavaScript was added. The optional shared PDF fallback respects the separate FlexCore.Documents package; trimmed hosts should pass PdfViewerComponentType explicitly.
- Shared Reports and chart changes are mirrored from FlexKit; project identities and the Documents split are preserved. Standard verification is 1,586 checks per library, plus 223 overlapping isolated parity checks. Audit: 18 pass, one known native analytical-import gap remains. Analytical export browser checks cover desktop/mobile fixed-paper geometry, not original Crystal visual approval. Inspector text binding and narrow-width editor/tab bounds have regression checks.
- Full ActiveReportsJS parity is NOT complete. No HomeFrontPB/HomeFront/Showcase host source or database was changed. Current status, build evidence and roadmap: /Users/wadood/projects/JavaToCSharp/converted/docs/activereportsjs-parity-implementation.md.

## 2026-09-17 Native Nested Flow And Graceful Defaults

- Nested sessions track their actual parents. Fixed-position child growth affects horizontally intersecting content, preserving independently growing columns and cached child queries.
- Physical replay schedules mutable section entry formatting before fields and ending page-break/reset conditions after fields. Hidden objects retain one-time formula assignments without visible output or growth; nested diagnostics reach the parent result.
- Main underlays now continue behind foreground pages with measured text fragments. Nested underlays retain overlap without advancing sibling flow. Oversized bottom-aligned sections anchor their final fragment; isolated trailing nested fragments do likewise.
- Conflicting continuations use explicit warnings and conservative defaults: preserve unrelated content/reserved space, keep already printed text immutable, omit unsafe newly visible tails, retain normal flow for shared bottom-aligned fragments, and bound unstable layout replay. Remaining malformed-input, resource and unsupported data-evaluation guards remain documented.
- Shared Reports sources match in FlexKit and FlexCore. Per library: 1,546 standard checks, 223 isolated parity checks (partly overlapping), 138 browser reflow checks and 38 unchanged metafile regression checks pass. Desktop/mobile screenshots inspected. Required non-incremental builds pass: HomeFront 203 warnings/zero errors; FlexCore.Showcase zero warnings/errors.
- This phase changes no images, binary readers, OS integration, packages, application JavaScript or host source; no website/database started. No Java, IKVM or SAP dependency added. Exact Crystal visual approval still requires matching originals. Current implementation and defaults: /Users/wadood/projects/JavaToCSharp/converted/docs/crystal-native-flow.md.

## 2026-09-17 Compound Paths And Nested Continuation

- Added pure C# EMF BeginPath/EndPath/CloseFigure/AbortPath and fill/stroke playback for bounded line, rectangle, polygon and cubic figures. Compound alternate/winding fills preserve holes; capture-time geometry and paint-time brush/pen/clipping remain distinct. Typed numeric path commands round-trip through XML and deep cloning. Unsupported glyph/ellipse outlines, pending-path DC save/restore, path widening/clipping and nonuniform wide pens remain explicit errors.
- Nested continuation suppression preserves horizontally independent columns and collapses only unused common tail space. Owned descendant ranges stop at suppression while neighboring ranges remain intact. Fixtures cover every depth through the existing eight-level nesting limit.
- Newly visible nested trees retain internal forced page breaks during changing-text reflow. Mandatory breaks carry adjacent text from the last complete measured line, including unchanged text, without clipping, duplication, rewriting printed prefixes or requerying children.
- Shared Reports sources match byte-for-byte in FlexKit and FlexCore. Per library: 1,538 standard checks, 215 isolated parity checks (partly overlapping), 116 browser reflow checks and 38 metafile pixel checks pass. Desktop/mobile screenshots inspected. Non-incremental HomeFront build: 203 existing warnings/zero errors; FlexCore.Showcase: zero warnings/errors.
- No host source, packages, application JavaScript, live website or database changed. No Java, IKVM, SAP or native graphics dependency added. EMF+, broader drawing operations and unrestricted arbitrary continuation remain incomplete; original Crystal visual approval still lacks matching baselines. Current details: /Users/wadood/projects/JavaToCSharp/converted/docs/crystal-text-visibility-continuation.md.

## 2026-09-17 Ship-Review Fixes (Update Repositories)

- Tabular Excel/CSV export skips on-demand subreport link columns (`IsSubreportObject`) and exports a column the query did not return as blank, as the viewer shows it. It used to throw "Missing export column '__Subreport_…'" (e.g. HomeFront `salessheetCurrentCosts Division version.xml`).
- Vector text is written as a `Value` attribute; `Read` still accepts the older element content. Whitespace-only or CR/LF text was lost on XML load, so Advances no longer matched and the report failed to load.
- Browser text-measurement batches are bounded by estimated returned lines (about 600, at 20 markup characters a line), as well as by 250 items, to stay under Blazor Server's default 32 KB hub message limit (HomeFront raises it; FlexCore.Showcase, FlexKitTester and NuGet consumers do not).
- Left open (review wf_af95484b-ac5, info): running totals use `OnFormula` in converted XML but `UseFormula` in the runtime/designer, and the formula text is not converted; the 100,000-measurement cap throws past the viewer's approximate-layout fallback; HTML export of a tabular report lacks the scoped viewer styling; the preview file name is taken from the title.
- Verified: ReportDesigner Regression/Parity/Component/Authoring/Export/Layout/Runtime suites on both libraries; a scratch round-trip check (spaces, CR/LF, tab; legacy element form) and export check.

## 2026-09-16 Advanced Metafile Records And Future Nested Trees

- Mirrored FlexKit's explicit clipped/opaque text, empty fills, ETO_PDY spacing, .NET single-byte charsets, transformed clip intersections and EMF Bezier variants. XML/clone/generated SVG validation retains bounded inert scenes. Unsupported graphics semantics remain explicit.
- Mirrored nested pre-flattening object evaluation, unprinted future child trees with repeating headers, effective child KeepTogether/entry conditions, ending-page NewPageAfter and intact fitting fixed-height fragments. Shared Reports sources match; branding and unrelated work are preserved.
- First-child/area NewPageBefore now honors preceding parent objects/sections, including zero inline offsets without breaking for hidden events.
- Each library passes 1,538 standard checks, 185 isolated (partly overlapping), 76 browser reflow and 33 image pixel checks. Non-incremental Showcase/HomeFront builds pass (HomeFront's existing 203 warnings only). No host edits, new packages/JavaScript or running website/database.
- Current implementation and remaining limitations: `/Users/wadood/projects/JavaToCSharp/converted/docs/crystal-text-visibility-continuation.md`. Full arbitrary continuation/metafile parity is not claimed. Original Crystal approval remains BLOCKED pending matching originals/data/parameters; 13 input/four comparator self-tests are synthetic only. No Java, IKVM or SAP dependency.

## 2026-09-16 Metafile Text, Mutable Visibility And Independent Continuations

- Mirrored FlexKit's bounded metafile text/font/spacing and affine transforms, saved drawing state, validated typed text/XML/generated SVG, physical mutable section/area visibility and hidden formula-field scheduling. Hidden subreport/mutable-format side effects remain explicitly guarded.
- Independent columns and newly visible future objects now continue without rewriting printed prefixes. Full-page hidden events use their actual fragment. Aggregate input analysis permits read-time summaries beside mutable print state while rejecting mutable summary inputs. Shared Reports sources match byte-for-byte; branding and unrelated work are preserved.
- Per library: 1,538 standard checks, 152 isolated (partly overlapping), 60 Chrome reflow and 27 bitmap/vector/text pixel checks pass. Required non-incremental builds: Showcase zero warnings/errors; HomeFront 203 existing warnings/zero errors. No host source, website or database was changed.
- Current scope and remaining gaps: `/Users/wadood/projects/JavaToCSharp/converted/docs/crystal-text-visibility-continuation.md`. Broader metafile text/GDI semantics and arbitrary nested continuation remain incomplete; original Crystal visual approval still needs matching renders/data/parameters. No Java, IKVM, SAP runtime, new packages or application JavaScript.

## 2026-09-16 Vector Pictures, Mutable Formatting And Inline Tails

- Mirrored FlexKit's bounded native WMF/EMF vector/OLE extraction, validated typed scenes and fixed generated SVG, vector Picture Expert preservation, physical mutable object formatting, CanGrow convergence and persistent inline-tail suppression/reflow. Shared Reports sources match; unrelated work and package metadata are preserved.
- Per library: 1,522 standard checks, 116 isolated (partly overlapping), 54 Chrome reflow and 15 bitmap/vector pixel checks pass. Required non-incremental builds pass: Showcase zero warnings/errors; HomeFront 203 existing warnings/zero errors. No website/database started or host source edited.
- Broader metafile text/fonts/transforms/composition, state-conditioned section visibility, mutable aggregates and arbitrary inline continuation remain incomplete. Synthetic vector pixels are not original Crystal visual approval.
- Current scope, reproduction and remaining gaps: `/Users/wadood/projects/JavaToCSharp/converted/docs/crystal-vector-mutable-inline.md`. No Java, IKVM, SAP runtime, native graphics package or application JavaScript was added.

## 2026-09-16 Phase-Aware Summaries, Bitmap Metafiles And Page Borders

- Mirrored FlexKit's phase-aware cached formula dependencies, pre-group-selection summaries, bounded pure-C# OLE DIB/single-bitmap WMF/EMF extraction, and box page-break closure. Shared Reports sources match byte-for-byte; package branding and unrelated work are preserved.
- Per library: 1,513 standard checks, 81 isolated (partly overlapping) parity checks, 39 Chrome reflow and two image-pixel checks pass. Required non-incremental builds: Showcase zero warnings/errors; HomeFront 203 existing warnings/zero errors. No website/database started.
- General vector metafiles, state-conditioned print evaluation and arbitrary inline cross-page continuation remain incomplete. Synthetic metafile fixtures/native screenshots do not establish Crystal visual parity; original renders with matched data/parameters are still missing.
- Current implementation, reproduction and remaining gaps: `/Users/wadood/projects/JavaToCSharp/converted/docs/crystal-parity-phases-metafiles.md`. No Java, IKVM, SAP runtime or new graphics dependency was introduced.

## 2026-09-16 Shared Print State And Text Continuation

- Mirrored FlexKit's measured growing/shared-inline physical replay, isolated child globals, stable print clock, and grapheme-offset rich-text continuation. Child queries are not re-executed by pagination. Branding and the Documents split are preserved.
- The existing measurement bridge is extended; C# retains scheduling/reflow. The fixture-driven native capture command and receipt checks bind report/data/parameters, output bytes and print environment. Original Crystal renders are still required for visual approval.
- Scope, explicit unsupported combinations and verification: `/Users/wadood/projects/JavaToCSharp/converted/docs/crystal-shared-growth-continuation.md`. No HomeFront application source, website or database is changed.
- Verified per library: 1,505 standard, 29 Chrome reflow, 49 bench/capture checks and four desktop/mobile bench cases. Visual gate: 13 synthetic input and four pixel checks; no Crystal approval claimed. Non-incremental Showcase and required HomeFront builds passed (HomeFront's existing 203 warnings only). Shared Reports/measurement sources match byte-for-byte.

## 2026-09-15 Tester Bench And Fixed-Geometry Page State

- Mirrored FlexKit's structured report-data adapter, positioned parameter/refresh workflow, stable preview identity, fixed-height mutable page replay, child-section KeepTogether and safe blank continuation boundaries. Package identity and the Documents split are unchanged.
- FlexKitTester `/crystal-reports` works against both library targets with browse/upload, fresh native conversion, designer/viewer and explicit data modes. Setup: `HomeFront/FlexKitTester/CRYSTAL-REPORTS.md`. No reference XML or Java/SAP runtime is used.
- Remaining limits are explicit: growing/state-conditioned mutable bands, shared inline page scheduling and changing-text continuation need further work. Crystal visual approval still needs original renders with matching data/parameters. Details: `/Users/wadood/projects/JavaToCSharp/converted/docs/crystal-bench-page-scheduling.md`.
- Verification on each library: 1,499 standard checks, 45 bench checks, 12 Chrome reflow checks and four desktop/mobile bench render cases pass. Required host builds pass (HomeFront: 203 existing warnings/zero errors; Showcase: zero warnings/errors). No website or live database was started; visual approval remains explicitly blocked on missing originals.

## 2026-09-15 Formula Scheduling and Measured Reflow

- Mirrored FlexKit's named-formula phases/dependency ordering, source-row read caches, and page-furniture variable snapshots. Invalid timing and speculative page-variable assignments are explicit errors.
- Viewer pagination now consumes measured glyph-line bounds, remeasures page-conditioned text, and retains child-owned section contexts for inline reflow. Per-page footer heights replace global maximum reservation. Project-specific metadata and the Documents satellite remain unchanged.
- Full completion is not claimed: physical-page mutable formula scheduling, character-offset continuation, page-conditioned child sections spanning pages, and approved Crystal visual baselines remain open. Details: `/Users/wadood/projects/JavaToCSharp/converted/docs/crystal-scheduling-reflow.md`.
- Both libraries pass 1,487 standard checks plus 12 Chrome reflow checks each. Required HomeFront/Showcase non-incremental builds pass. No website was started or HomeFront host source changed.

## 2026-09-15 Crystal Authoring, Totals and Native Exports

- Mirrored FlexKit's multi-object editing/arrangement/clipboard, transactional section lifecycle with opaque XML preservation, and actual Running Total Expert authoring. Runtime evaluates conditional running totals, logical page resets, group keep-together, relative measured growth and page-dependent rich-run styles. Native totals retain condition field/group operands.
- Phase 5 now has genuine HTML/CSV/XLSX exports through the existing native exporter and per-tab data snapshots. Print / Save as PDF remains explicitly browser-driven. Project-specific package/assembly metadata and the Documents satellite split were preserved.
- Full phase completion and Crystal visual parity are not claimed. Scheduler, exact splitting/inline reflow, analysis rendering and broader exports remain open. Scope and verification: `/Users/wadood/projects/JavaToCSharp/converted/docs/crystal-designer-phase34-followup.md`.
- Final verification: 1,471 checks per library (2,942 combined), desktop/mobile browser checks including 67 toolbar assets, and both required non-incremental solution builds passed. HomeFront: 203 warnings/zero errors; Showcase: zero warnings/errors. Shared Reports and keyboard-bridge sources match byte-for-byte. No websites started.

## 2026-09-15 Crystal Chart and Cross-tab Diagnostics

- Legacy native chart/cross-tab objects now retain identity, bounds, common formatting and opaque decoded-stream object bytes, with scoped conversion diagnostics in the model, XML and progress callback. Metadata probing is isolated and missing terminators have explicit recovery warnings.
- Both runtime modes diagnose unsupported analysis objects, including suppressed ones. Positioned output uses escaped placeholders when visible; tabular output omits the objects. Analysis-specific formulas stay opaque, and source XML survives targeted editing. This does not implement chart rendering or pivot/cell evaluation.
- Shared report changes are mirrored between FlexKit and FlexCore; the standalone JavaToCSharp converter has the same retention/diagnostic additions. Exact behavior, reproducible synthetic fixtures and validation: `/Users/wadood/projects/JavaToCSharp/converted/docs/crystal-unsupported-analysis-objects.md`.

- Verification: 597 preservation, 113 component, 172 layout and 474 runtime checks passed per library (1,356 each). Standalone: 65 native checks and 19/19 normalized Java-reference matches. Both required non-incremental solution builds passed; HomeFront had 203 warnings/zero errors, Showcase zero warnings/errors.

## 2026-09-13 Pointer Selection Bench And Promotion

- HHM-930 was prototyped in FlexCore/FlexKitTester only, then explicitly approved by the owner for promotion to FlexKit. The five shared grid files now match byte-for-byte; no further selection algorithm changes were introduced during promotion.
- Instant row feedback suppresses fading and reasserts the newest row before paint when delayed selection/drag replies arrive. A generation acknowledgement waits for the existing click handlers (including host validation) and releases the preview only after that render; it does not select records or change selection rules.
- FlexKitTester's picklist and row-selection benches expose the existing simulated uplink delay; the picklist also offers Single/Multiple selection. Browser regression checks are in `HomeFront/FlexKitTester/verification/pointer-selection-latency.mjs`.
- Final Chrome run: 156 assertions, 3,376 sampled frames without overlapping/stale highlights, using 0/150/400/1,000 ms delay each way, plus the existing 250 ms uplink simulator. Covers revisits, held/cancelled presses, Ctrl/Cmd/Shift, keyboard navigation, reopening, the 1,000-row/50-column selection bench, drag ranges and dirty-row veto. The original FlexKit module fails the same picklist test (33 bad frames).
- Promotion verification against FlexKit: 156 assertions, 3,366 sampled frames without stale/overlapping highlights using the same latency and interaction matrix. Results: `/tmp/hhm930-flexkit-promoted/summary.json`. FlexKitTester builds against FlexKit (existing ColumnSwapBench warning); HomeFront and FlexCore.Showcase non-incremental builds pass. The earlier unrelated report build blocker is no longer present. No HomeFront source, database data or user-run process was changed.

## 2026-09-13 Report Designer Phase 1

- Mirrored FlexKit's non-mutating XML preservation, canonical designer/runtime formula/selection/parameter/sort/summary contracts, command/alias/schema handling, composite joins, bound selection values and explicit grand totals. Production generated SQL has no preview row cap and is not automatically saved as a custom override.
- The formula editor now edits bodies and reports known runtime-subset limitations. Full Crystal formula/layout parity, dependency-safe rename and save/preview transactions remain later phases.
- JavaToCSharp's source-linked report regression harness passed 358 assertions on each library across all 29 reference XMLs; the real-component HtmlRenderer harness passed 29 expert/undo/executor checks on each. Required non-incremental HomeFront and FlexCore.Showcase builds passed. No database, website startup, reference-XML rewrite or host-source edits. Details: `/Users/wadood/projects/JavaToCSharp/converted/docs/crystal-designer-phase1.md`.

## 2026-09-13 Crystal Designer Phase 2

- Mirrored FlexKit's explicit save-result/atomic persistence contracts, conflict and replacement guards, saved undo checkpoints, isolated preview packages and portable subreports. The Showcase bench retains its designer across tabs and runs the current design without implicitly saving it.
- Mirrored transactional Text Format, lock enforcement, field/explorer synchronization, safe group/area reconciliation, page setup geometry, A3 and stored-title fallback. Reports sources match FlexKit; no Java, IKVM, SAP report engine or new JavaScript was introduced.
- Each library passed 586 native regression assertions and 98 component checks across 29 XML files plus RPT-only Job Budget conversion/workspace lifetime. Showcase, HomeFront and HomeFrontPB builds passed. Browser/PDF visual parity and the positioned report layout engine remain later work. Details: `/Users/wadood/projects/JavaToCSharp/converted/docs/crystal-designer-phase2.md`.

## 2026-09-13 Crystal Designer Phase 3

- Mirrored FlexKit's move/resize/zoom/snap canvas, image and rich-run XML authoring, native RPT run capture, shared visual renderer, positioned query path and band pagination. Designer preview and Showcase's Crystal bench Run tab use positioned output; ordinary tabular report callers remain compatible.
- The lazy `report-layout.js` performs DOM-only pointer capture/text measurement. Edit transactions, fields/summaries and page placement remain C#, with no Java/IKVM/SAP runtime or added NuGet dependency. Native binary pictures, full conditional/formula/subreport layout and exact cross-page splitting remain open.
- Validation and limitations: `/Users/wadood/projects/JavaToCSharp/converted/docs/crystal-designer-phase3.md`. No HomeFront host source edits or website startup in this phase. Full visual parity requires matching Crystal-rendered references, not only XML round trips.
- Each library passed 586 preservation, 113 component and 172 layout checks, including 29 XMLs, all 19 current RPTs and the Showcase synthetic-data provider. Desktop/mobile Chrome checks verified measured four-page output and no band overlaps. Required non-incremental HomeFront/Showcase builds passed.

## 2026-09-14 Crystal Designer Phase 4

- Mirrored FlexKit's linked native RPT picture extraction, C# Crystal formula/conditional engine and inline subreport pagination. The formula parser was Sprache 2.3.1 then; since 2026-09-25 it is hand-written with no parser package. Project-specific packaging and the Documents satellite split are preserved.
- Positioned reports evaluate selection, sorting, formulas, summaries and supported conditions natively. Subreports use bound links, execution-local shared variables, bounded/cached queries, child-band page breaks and repeated group headers. Existing tabular callers remain unchanged.
- Native font/border/section/object condition readers retain formula newlines. The designer checks the native grammar; no new application JavaScript or host-source changes. Detailed verification and explicit remaining gaps: `/Users/wadood/projects/JavaToCSharp/converted/docs/crystal-designer-phase4.md`. Do not claim full Crystal value/visual parity from syntax or synthetic fixtures.
- Final verification matches FlexKit: 586 preservation, 113 component, 172 layout and 396 runtime checks (1,267 per library), all 19 RPTs and 12 native picture payloads. Chrome passed the four-page layout/inline fixtures on desktop/mobile with zero band overlaps. Non-incremental Showcase build has zero warnings/errors; HomeFront also passed with its 203 warnings. Shared Reports directories are identical.

## 2026-09-13 Header Paint Verification

- HHM-1044: Mirrored FlexKit's opaque sticky-header background and one-pixel upward box shadow to cover the collapsed-table-border paint gap. CSS only; no grid editing, selection, or scrolling logic changed.
- FlexCore.Showcase build passed; GridControl CSS matches FlexKit. HomeFront's `verification/R2QaChecks/header-paint-browser.mjs` passed 240 Chrome pixel/scrolling checks against FlexKit on Model and Options and Custom Requests across viewport widths, display scales and 150ms latency; the old CSS fails the pixel check. No data or layouts saved.

## 2026-09-13 Chained Prompt Verification

- Mirrored InputDialogControl's fresh keyed client-buffered textbox, post-close result completion, single Escape/focus owner, and prompt-local dotted OK/Cancel focus outline from FlexKit. No GridControl, global ButtonControl, or caller cancellation-policy changes.
- FlexCore.Showcase build passed and the shared prompt source matches FlexKit. HomeFront's isolated `verification/InputDialogBrowserChecks` fixture passed 300 Chrome checks against FlexKit at 1100px and 480px without a database, covering chained defaults, closed renders, Escape, callbacks, buffered confirmation, and keyboard focus.

## 2026-09-13 Date Editor Verification

- HHM-934: Mirrored DatePickerControl's shrinkable input and non-shrinking calendar trigger from FlexKit. No GridControl or keyboard logic changed. FlexCore.Showcase build passed; shared Chrome coverage in HomeFront passed 84 layout/navigation checks at 1500px and 1067px. The reported focus trap was not reproduced.

## 2026-09-12 Control Verification

- HHM-1018: Mirrored the classic InputDialogControl composition and TextBoxControl `FlushClientBufferedValueAsync` from FlexKit. The prompt uses client-buffered typing, explicit OK flush, and native Tab traversal; GridControl is unchanged.

- MessageBoxControl is draggable and keeps background-click dismissal opt-in via `CloseOnOverlayClick`; keyboard behavior is unchanged.
- RadioControl labels display a dotted focus outline without changing layout.
- TextBoxControl `ShowPasswordToggle` defaults to true; false hides the toggle and forces masked display even if previously revealed. `RevealLastTypedCharacter` remains a separate option.
- Deferred scrollbar cancellation calls back into the `registerGridWindowScroll` closure; its `scheduled` variable is not in scope inside `createDeferredGridScrollbar`.
- Full FlexCore.Showcase build passed with zero errors. Shared source matches FlexKit for this batch; active HomeFront verification covers the shared controls without database commits.
