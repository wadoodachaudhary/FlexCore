# FlexCore Work Notes

Follow `/Users/wadood/projects/VBToCSharp/AGENTS.md`. Shared code is mirrored from
FlexKit; preserve project-specific files and build FlexCore.Showcase after edits.

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
