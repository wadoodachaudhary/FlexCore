# Remaining control implementation

Scope: implement the 32 confirmed missing dedicated controls from the verified parity backlog, with an independent FlexKitTester bench for every control. MediaPlayer and ActionSheet are also tracked separately as broader-catalog additions. Existing feature-level gaps are not automatically closed by adding a component type.

## Work tracking

- [x] Shared popup, tooltip, popover, browser-capability bridge
- [x] Layout, loading, compact content and selection controls
- [x] Masked/date-range/tree/range/color/signature inputs
- [x] Upload and external DropZone
- [x] PromptBox, Chat, InlineAIPrompt, SmartPaste, SpeechToText
- [x] Spreadsheet backed by existing ClosedXML
- [x] Scheduler with recurrence and resource views
- [x] Map with geographic layers
- [x] Separate FlexKitTester pages and navigation
- [x] Model tests, browser interactions and required builds

## Reference contract

Implementation uses the supplied audit and the official [Spreadsheet](https://www.telerik.com/blazor-ui/documentation/components/spreadsheet/overview), [Scheduler](https://www.telerik.com/blazor-ui/documentation/components/scheduler/overview), and [Map](https://www.telerik.com/blazor-ui/documentation/components/map/overview) documentation as behavior references. This is an independent FlexCore implementation, not a source port of proprietary Telerik components.

## Verification result

- 32 independently routed FlexKitTester benches render and have exercised interactions.
- 168 C# regression checks passed, including 37 new model checks.
- 70 new-control browser checks passed; the previous 29 counterpart checks and 202 grid/parity checks also passed.
- FlexKitTester and FlexCore.Showcase builds passed.
- API details and remaining feature-level parity differences are recorded in [remaining-controls.md](remaining-controls.md).

Component availability is complete for this 32-control scope. Complete Telerik feature/API parity is not established by these checks; the guide preserves the remaining work explicitly.
