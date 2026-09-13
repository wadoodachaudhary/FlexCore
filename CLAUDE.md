# FlexCore Work Notes

Follow `/Users/wadood/projects/VBToCSharp/AGENTS.md`. Shared code is mirrored from
FlexKit; preserve project-specific files and build FlexCore.Showcase after edits.

## 2026-09-12 Control Verification

- MessageBoxControl is draggable and keeps background-click dismissal opt-in via `CloseOnOverlayClick`; keyboard behavior is unchanged.
- RadioControl labels display a dotted focus outline without changing layout.
- TextBoxControl `ShowPasswordToggle` defaults to true; false hides the toggle and forces masked display even if previously revealed. `RevealLastTypedCharacter` remains a separate option.
- Deferred scrollbar cancellation calls back into the `registerGridWindowScroll` closure; its `scheduled` variable is not in scope inside `createDeferredGridScrollbar`.
- Full FlexCore.Showcase build passed with zero errors. Shared source matches FlexKit for this batch; active HomeFront verification covers the shared controls without database commits.
