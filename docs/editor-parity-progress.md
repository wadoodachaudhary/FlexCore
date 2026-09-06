# EditorControl: first Syncfusion parity increment

This increment extends the existing FlexCore block editor. It addresses shared editing behaviors documented by Syncfusion's [find/replace](https://help.syncfusion.com/document-processing/word/word-processor/blazor/find-and-replace) and [history](https://help.syncfusion.com/document-processing/word/word-processor/blazor/history) APIs. Word/DOCX import, document layout fidelity, tables, review/track changes and a Word processing engine remain separate work.

## Added behavior

- Optional FlexCore toolbar with formatting, paragraph alignment, undo/redo and a find/replace panel.
- Literal search across inline formatting, with case-sensitive and Unicode whole-word options, match counts, result selection, and next/previous navigation. Matching is within logical blocks, not across distinct paragraphs. CSS Highlight support highlights all matches; native selection identifies the active result.
- Replace current and replace all. Replacement strings are literal text; replace-all is a single history transaction and preserves formatting outside the replaced ranges.
- Opt-in history for typing, formatting and replacements. Undo/redo works through the API, toolbar, Ctrl/Cmd+Z, Ctrl/Cmd+Shift+Z and Ctrl/Cmd+Y. Adjacent typing is coalesced; history depth is bounded.
- Read-only mode blocks user editing and editing commands while permitting selection/search. Explicit host document loads remain available.
- Accessible editor labeling, multiline/read-only state, unique default instance IDs and automatic loading of the existing editor bridge.

## Corrected behavior

- `OnBlocksChanged` now supplies the documented block snapshot instead of an empty list. Echoing that same snapshot into `Blocks` does not overwrite the DOM or reset typing.
- Readback retains the sanitized inline markup in `Html`, so reading and pushing a snapshot preserves formatting. Literal HTML characters and soft line breaks survive the round trip.
- PageBreak is restored as PageBreak in C# readback.
- Browser-created paragraphs receive distinct IDs, including when Enter clones the original paragraph attributes.
- Layout changes use current editor contents and retain edit history, rather than loading stale seed blocks. Trailing empty page affordances do not become extra saved paragraphs.
- Paragraph alignment/style operations target blocks inside paginated pages.
- Caret and block-scroll APIs are scoped to their editor, even when two editors share block IDs.
- Explicit document loads persist through unrelated parent renders rather than reverting to the original seed content.

## API and compatibility

`ShowToolbar` and `EnableHistory` default to **false** to preserve existing applications with their own toolbar/history. Enable both for the integrated experience. `HistoryLimit` defaults to 100. `ReadOnly`, `SpellCheck` and `AriaLabel` configure the surface.

New methods: `GetHistoryStateAsync`, `UndoAsync`, `RedoAsync`, `FindAllAsync`, `SelectSearchResultAsync`, `ReplaceCurrentAsync`, `ReplaceAllAsync`, `ClearSearchAsync` and `OpenSearchAsync`. `HistoryChanged` exposes undo/redo availability and counts.

`PushAsync` explicitly loads a document and resets component history. Toggling pagination preserves content/history. Existing hosts that manage external histories can leave `EnableHistory=false`. History uses bounded block snapshots; very large document performance is not certified by this increment.

DOM range selection, contenteditable transactions and measurements use the existing `wwwroot/editor-control.js` bridge. The bench contains no custom JavaScript or hand-built input controls.

## Bench and verification

Open **FlexCore control benches → EditorControl** at `/flexcore-controls/editor` in FlexKitTester.

The bench includes formatted text, literal characters, Unicode words, a soft line break, an explicit page break, snapshot save/reload/readback, binding echoes, a paginated view and a second editor with a shared block ID.

The dedicated browser harness links that exact bench rather than maintaining a separate demo implementation:

Verified: **16 browser checks in Chromium and 16 in WebKit**, including editing, formatting, search/replace, undo/redo, read-only behavior, pagination, paragraph identities and instance isolation.

```sh
cd /Users/wadood/projects/VBToCSharp/FlexCore/tests/FlexCore.Editor.BrowserTests
dotnet build
dotnet bin/Debug/net10.0/FlexCore.Editor.BrowserTests.dll
FLEXCORE_BROWSER=webkit dotnet bin/Debug/net10.0/FlexCore.Editor.BrowserTests.dll
```

The harness runs on a temporary loopback port and shuts itself down after verification. It does not restart the user's tester application.
It roots the test server's generated CSS preload hint so WebKit does not request a second stylesheet relative to the deep bench route; the real compiled styles are still loaded and checked.
