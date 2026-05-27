# FlexCore

General-purpose, application-agnostic Blazor controls for .NET — grids, tree-grids, charts, diagrams, reports, dialogs, ribbons, toolbars, notifications, multi-select dual-list, editors, and more.

```csharp
using Fx.ControlKit.Grid;
using Fx.ControlKit.Notifications;
```

## Highlights

- **GridControl** — virtualized data grid with sorting, filtering, grouping, drag-reorder, header context menu, in-line / batch / inline editing, choose-columns dialog, aggregate footers
- **TreeGridControl** — hierarchical version of the grid
- **ReportWriterControl** — adaptive paginated report renderer fed by a small `ReportDefinition` (loaded from Crystal XML, plain SQL, or any other source you wire)
- **DialogControl, NotificationService, RibbonControl, ToolbarControl, ButtonControl, DropDownListControl, MultiSelectDualListControl, ChartControl, DiagramControl, EditorPanelControl, OutlineControl, ProgressBarControl, PropertyGridControl, TabsControl** — the rest of the kit
- **App-agnostic by design** — every external dependency (DB, session, picklist source, report exporter) is exposed as an interface; host apps wire their own adapters in `Program.cs`

## Targets

- `.NET 10.0`
- Blazor Server or Blazor WebAssembly

## Install

Once published to NuGet:

```bash
dotnet add package FlexCore
```

Or reference the project directly:

```xml
<ProjectReference Include="..\path\to\FlexCore\FlexCore.csproj" />
```

## Quick start — a grid

```razor
@using Fx.ControlKit.Grid

<GridControl TValue="MyRow" DataSource="@rows"
             AllowSelection="true" AllowSorting="true"
             AllowFiltering="true" AllowGrouping="true">
    <GridColumnsBase>
        <GridColumn Field="Id"       HeaderText="ID"       Width="80px" />
        <GridColumn Field="Name"     HeaderText="Name"     Width="200px" />
        <GridColumn Field="Quantity" HeaderText="Qty"      Width="100px"
                    Type="ColumnType.Number" Format="N0" TextAlign="TextAlign.Right" />
    </GridColumnsBase>
</GridControl>

@code {
    record MyRow(int Id, string Name, int Quantity);
    List<MyRow> rows = new() { new(1,"Foo",10), new(2,"Bar",20) };
}
```

## License

MIT — see [LICENSE](LICENSE).
