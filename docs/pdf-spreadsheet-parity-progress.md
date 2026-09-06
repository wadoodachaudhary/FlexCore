# PDF Viewer and Spreadsheet: first Syncfusion parity increment

This extends the existing controls rather than introducing duplicate counterparts. It follows the [5 September audit](syncfusion-parity-audit.md), focusing on PDF search/navigation and the Spreadsheet freeze defect and filtering gap. The original audit remains a baseline, not a claim that every document feature is now equivalent.

Reference behavior: Syncfusion's [PDF search API](https://help.syncfusion.com/cr/blazor/Syncfusion.Blazor.SfPdfViewer.PdfViewerBase.html), [bookmark navigation](https://help.syncfusion.com/document-processing/pdf/pdf-viewer/blazor/interactive-pdf-navigation/bookmark) and [Spreadsheet filtering](https://help.syncfusion.com/document-processing/excel/spreadsheet/blazor/filtering). The freeze change corrects an existing FlexCore command; it does not assert a one-to-one Syncfusion Blazor freeze API match.

## PDF Viewer

- Search now reports individual literal occurrences, including several on the same page. Next/previous wraps through results and updates the current page. Enter searches; Escape cancels.
- Match-case support, active-result indication, precise occurrence highlights and a search-state callback. Text split across font changes shares one searchable offset map. Highlights follow zoom and all page rotations.
- Nested document bookmarks appear in the Details sidebar. Internal destinations navigate to pages. Group headings or unsupported external destinations remain visible and disabled; document-supplied URLs/actions are not executed.
- The existing PDF.js bridge uses `ReadableStream.getReader()` for text extraction, including WebKit versions without async stream iteration. Explicit text-layer sizing and rotation keep selection geometry aligned with the page. No new JavaScript file or dependency was added.
- Existing form edits, notes/highlights, download and saved-byte reload remain available.

Additive API:

```csharp
await viewer.SearchTextAsync("invoice", matchCase: false);
await viewer.SearchNextAsync();
await viewer.SearchPreviousAsync();
await viewer.GoToSearchMatchAsync(0); // zero-based occurrence index
await viewer.CancelTextSearchAsync();
PdfSearchState state = viewer.SearchState;
IReadOnlyList<PdfBookmark> outline = viewer.Bookmarks;
```

`SearchChanged` reports query, case option, matches, zero-based current index (`-1` when empty), and `IsTruncated`. `ShowBookmarks` defaults to true. `Loaded` now includes bookmarks. The existing parameterless `FindAsync()` still searches the toolbar's query.

Search results are bounded to 200 occurrences or approximately 24 KB of result JSON, whichever is reached first, to stay within normal Blazor Server message limits. The toolbar/API explicitly report truncation. PDF text extraction order governs matching; this is not OCR, linguistic normalization or certification of every PDF's reading order. Bookmark navigation targets a page, not the destination's exact zoom/coordinates. Continuous page layout, thumbnails, hyperlink overlays, on-page form controls, form design, public annotation history/import/export, page organization, redaction and cryptographic signing remain open work.

## Spreadsheet

- Freeze/Unfreeze now controls visible rows and columns. CSS sticky positioning fixes both axes during scrolling; frozen cells remain present when paging the bounded viewport. Imported freeze settings and undo/redo feed the same rendering path.
- Filter column opens a FlexCore dialog for the header-inclusive range, absolute column letter, condition and value. Supported conditions are Contains, Equals and NotContains. Matching ignores case. Contains/NotContains treat wildcard characters literally; Equals uses exact displayed-value matching.
- Separate column criteria combine with AND. Clear column preserves other criteria; Clear filters removes the filter. Hidden rows and columns are omitted from the viewport and arrow navigation skips them.
- Edits reapply filters, including recalculating formula cells in the filter area. Filter criteria, hidden rows and freeze settings survive undo, saved snapshots and XLSX download/reopen. Protected sheets and read-only controls reject these mutations.
- The shared ButtonControl hover style now retains its default border width, preventing wrapped toolbar buttons from moving between rows during a click.
- ClosedXML 0.105's custom Equals path also accepts longer strings; exact matching uses its regular filter API. Its cleared `FilterType.None` columns cannot be saved. Clearing therefore removes the requested OOXML criteria and reloads the workbook, preserving other imported filter configurations.

Additive control API:

```csharp
await sheet.FreezeAsync(1, 1); // first row and column
await sheet.ApplyTextFilterAsync(new(1, 1, 121, 14), 2, "North");
await sheet.ClearFilterAsync(2); // absolute column number
await sheet.ReapplyFiltersAsync();
await sheet.ClearFiltersAsync();
await sheet.FreezeAsync(0, 0);
```

`AllowFiltering` defaults to true. `Document` also exposes synchronous `ApplyTextFilter`, `ClearFilter`, `ClearFilters`, `ReapplyFilters`, `Freeze`, `FrozenRows` and `FrozenColumns`. These use the existing `Changed`, undo and error-reporting paths. A different active filter range must be explicitly cleared before replacing it.

The viewport remains bounded by `VisibleRows`/`VisibleColumns`, with paging rather than scroll-driven virtualization. Cells use fixed 30-pixel rows and 120-pixel columns; resizing remains a separate gap. Oversized frozen areas display an explicit window-limit message and reserve space for an unfrozen row/column when the configured RowCount/ColumnCount permit it. Merged cells are clipped to the rendered window. Large-workbook performance and complex merges spanning frozen boundaries are not certified. History and filter clearing use full workbook snapshots. Excel-style value checklists, numeric/date/custom compound filters, table-specific filter UI, conditional formatting, named-range management, image/hyperlink authoring and broader file/print formats remain open work.

## FlexKitTester benches and verification

- **PDF Viewer:** `/flexcore-controls/pdf`. A three-page PDF includes mixed-case repeated text, a phrase split across fonts, a form field and nested bookmarks. Test toolbar/API search, case, navigation, save/reload and download.
- **Spreadsheet:** `/flexcore-controls/spreadsheet`. The formula sample remains. A second sample has 120 data rows and 14 columns for two-axis scrolling, freeze/paging, combined Region/Item filters, undo, save/reload and read-only guards.

All host inputs, buttons, selectors, checkboxes and dialogs use FlexCore. The control catalog has 37 entries, including the later tree benches. The browser harness links the actual bench files and checks downloaded files with `pypdf` and `openpyxl`, in addition to DOM interactions and real scroll geometry.

```sh
cd /Users/wadood/projects/VBToCSharp/FlexCore/tests/FlexCore.Documents.BrowserTests
dotnet build
dotnet bin/Debug/net10.0/FlexCore.Documents.BrowserTests.dll
FLEXCORE_BROWSER=webkit dotnet bin/Debug/net10.0/FlexCore.Documents.BrowserTests.dll
```

Original increment verification: 10 document browser checks in Chromium and 10 in WebKit; 187 model/render regression checks; and 12 existing PDF annotation/form browser checks. FlexCore.Showcase and FlexKitTester builds pass.

The harness uses a temporary loopback host and shuts it down on completion. It does not start or restart the user's application. The existing model regression suite and PDF annotation/form browser checks also cover compatibility with prior behavior.


## Navigation and disposal correction

PDF cleanup now uses a unique viewer session ID instead of resolving a DOM `ElementReference` after Blazor has removed it. Cleanup is idempotent and null-safe, cancels fetches/renders, destroys PDF.js loading tasks/workers, and removes both session and element mappings. A module import completing after disposal is released, and late loading/rendering callbacks cannot update the removed viewer. Existing search, forms, save/reload and spreadsheet behavior remain covered by the document suite.

The shared bench frame exposes an **All controls** button returning to `/flexcore-controls`. The browser fixture now routes within one live circuit, using the actual catalog and PDF/TreeView/TreeGrid/TreeGrid operations benches. It checks repeated PDF → catalog → tree → PDF transitions, verifies detached PDF state is released, and leaves during a delayed PDF fetch. Previously the tests used full `page.goto` reloads between benches, which did not exercise Blazor's component disposal path.

Navigation-fix verification: 14 document browser checks in Chromium and 14 in WebKit, including the new navigation/cleanup cases. FlexCore.Showcase and FlexKitTester builds pass with existing warnings only.
