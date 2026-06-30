# HomeFront & FlexKit Developer Rules

These rules apply universally when coding, building, and debugging within the HomeFront (PB, Mobile Source) and FlexKit/FlexCore codebase.

## 1. VB6 Fidelity — The Prime Directive
- **100% Visual and Behavioral parity**: Parity to VB6 in look, feel, and behavior. Always read the matching `.frm` / `.bas` files before writing or troubleshooting a migrated page.
- **Dynamic Options and Layouts**: Never hardcode values derived at runtime (e.g., App-Options, DB values, AppGridLayout).
- **No Hardcoded Grid Columns**: Grid columns must be populated dynamically via `AppGridLayout` (`GetGridLayoutAsync` / `IniGetGrid`). Never use static `<GridColumn>` markup.
- **Exact Identifier Names**: Keep VB6 form and control names as lookup keys in `AppGridLayout` (e.g., `FAddPricelist.gItems`).
- **Visible Fallback Markers**: Do not suppress errors silently; render visible fallbacks (e.g., `[gItems FALLBACK]` in titles) when default coordinates/configurations are missing.

## 2. FlexKit as the Sole UI Control Source
- **No Raw HTML Input Controls**: All UI controls must resolve to FlexKit components (`Fx.ControlKit.*`) and use standard `fx-*` layout CSS. Never hand-roll custom input, select, or overlay modal controls.
- **Dialogs & Modals**: Modals must be instantiated using `DialogControl` or by hosting an entire form (`F*`) within it.
- **Parameter Rendering Constraints**: Only `TextBoxControl` forwards arbitrary HTML attributes (such as `style`). Other FlexKit components will throw rendering exceptions if arbitrary attributes are passed—always use the components' built-in parameters (`Width`, `CssClass`, etc.).
- **Shared Layout Presenter**: Leverage the shared `GridLayoutPresenter` service for grid layouts rather than duplicating layout code locally.

## 3. Minimal JavaScript
- **Prefer Native Blazor/CSS**: Rely on native Blazor features (`FocusAsync`, `@ref`, `@bind`, etc.) and CSS selectors (`position: sticky`, `:focus-within`) before turning to JS.
- **JS Fallbacks Only**: Use JavaScript only if no C# or CSS equivalent exists. Wrap it in a lazily-imported module with a C# fallback. Avoid adding new script files or inline `<script>` blocks without explicit warning.

## 4. Kept in Sync & Multi-Target Builds
- **FlexKit ↔ FlexCore**: Library source files must be byte-identical. Only project metadata (.csproj) and VCS (.git) may differ. Mirrors must be updated in the same change set.
- **HomeFront ↔ HomeFrontPB**: Mirror migrated form fixes between both host applications.
  - HomeFront path: `Components/Pages/Migrated/F*.razor`
  - HomeFrontPB path: `Components/Pages/F*.razor`
- **Build Checks**: After editing FlexKit, run non-incremental builds (`dotnet build --no-incremental`) on both `HomeFront.sln` and `HomeFrontPB.sln` to catch compile regressions early. Rebuild `FlexCore.Showcase.sln` after FlexCore edits.
- **Documentation**: Keep both `CLAUDE.md` files current.

## 5. App Status & General Scope
- **Active App**: `HomeFrontPB` is active.
- **Archived App**: `HomeFrontPOC` is archived; do not modify, build, or deploy it.
- **Namespaces**: Keep namespaces unified under `Fx.ControlKit.*` and CSS classes prefixed with `fx-*`.

## 6. Security, SQL & DB State
- **Parameterize All SQL**: Never interpolate user input directly into SQL statements.
- **No Inline Schema Modification**: Schema migrations must not run from the application layer.
- **Read-Only Report XML**: Do not edit report XML documents directly (they regenerate from crystal reports).
- **No Static User State**: To prevent cross-tenant data leaks in Blazor Server, do not store user-specific variables in static fields. Use scoped services (`ISessionStateService`).

## 7. Operations & Host Independence
- **Execution Policy**: Do not auto-start `dotnet run` or application execution without the user's explicit request. Building/compiling is fine. Kill any background run processes when completed.
- **Artifacts**: Do not generate modified-files lists or deployment zips unless requested.
- **No Host Dependencies in Libraries**: FlexKit/FlexCore must remain general-purpose and never reference HomeFront namespaces or classes directly. Use interfaces and adapters.
