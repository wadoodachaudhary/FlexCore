# FlexCore Work Notes

Follow `/Users/wadood/projects/VBToCSharp/AGENTS.md`. Shared code is mirrored from
FlexKit; preserve project-specific files and build FlexCore.Showcase after edits.

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

- Mirrored FlexKit's linked native RPT picture extraction, C# Crystal formula/conditional engine and inline subreport pagination. Sprache 2.3.1 is a managed MIT parser dependency, not a Crystal/Java runtime; project-specific packaging and the Documents satellite split are preserved.
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
