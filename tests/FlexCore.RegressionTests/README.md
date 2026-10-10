# Component regression checks

Run from the FlexCore repository:

```sh
dotnet run --project tests/FlexCore.RegressionTests/FlexCore.RegressionTests.csproj
```

The runner hosts real components in Blazor's HtmlRenderer, dispatches component events, and exits nonzero on failure. No web server is required. SVG barcode output is rasterized and decoded; filtering is checked against typed records; pivot headers and aggregates are checked after sorting; grid confirmation and editing are checked against actual rendered state and callbacks.

Program preview checks also launch real processes. The GUI check needs `xvfb`, `xdotool`, `ffmpeg` or `xwd` (`x11-apps`), and `python3-tk`. A side-by-side page for the browser is `tests/FlexCore.PreviewHarness`. Package details are in [the preview guide](../../docs/program-preview.md).

Internal handler access is limited to this fixture so production APIs do not need test-only hooks. Test source is excluded from the library's default SDK items.
