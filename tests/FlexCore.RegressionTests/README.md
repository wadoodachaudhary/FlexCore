# Component regression checks

Run from the FlexCore repository:

```sh
dotnet run --project tests/FlexCore.RegressionTests/FlexCore.RegressionTests.csproj
```

The runner hosts real components in Blazor's HtmlRenderer, dispatches component events, and exits nonzero on failure. No web server or application startup is required. SVG barcode output is rasterized and decoded; filtering is checked against typed records; pivot headers and aggregates are checked after sorting; grid confirmation and editing are checked against actual rendered state and callbacks.

Internal handler access is limited to this fixture so production APIs do not need test-only hooks. Test source is excluded from the library's default SDK items.
