# FlexCore Work Notes

Follow `/Users/wadood/projects/VBToCSharp/AGENTS.md`. Shared code is mirrored from
FlexKit; preserve project-specific files and build FlexCore.Showcase after edits.

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
