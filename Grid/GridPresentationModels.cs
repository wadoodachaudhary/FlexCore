using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Grid;

/// <summary>Controls where GridControl renders its pager.</summary>
public enum GridPagerPosition
{
    Bottom,
    Top,
    Both
}

/// <summary>Controls the visual arrangement of row fields.</summary>
public enum GridDataLayoutMode
{
    Columns,
    Stacked
}

/// <summary>Enables viewport-driven presentation changes without altering data behavior.</summary>
public enum GridAdaptiveMode
{
    None,
    Auto
}

/// <summary>Commands that a host can bind to custom keyboard shortcuts.</summary>
public enum GridKeyboardCommand
{
    MoveLeft,
    MoveRight,
    MoveUp,
    MoveDown,
    FirstRow,
    LastRow,
    PageUp,
    PageDown,
    BeginEdit,
    SaveEdit,
    CancelEdit,
    SelectAll,
    ClearSelection,
    ExpandDetail,
    CollapseDetail
}

/// <summary>Public, immutable pager-template context.</summary>
public sealed class GridPagerContext
{
    internal GridPagerContext(
        int currentPage,
        int totalPages,
        int pageSize,
        int totalRecords,
        IReadOnlyList<int> pageSizes,
        IReadOnlyList<int> pageNumbers,
        string rowCountText,
        Func<int, Task> goToPageAsync,
        Func<int, Task> setPageSizeAsync)
    {
        CurrentPage = currentPage;
        TotalPages = totalPages;
        PageSize = pageSize;
        TotalRecords = totalRecords;
        PageSizes = pageSizes;
        PageNumbers = pageNumbers;
        RowCountText = rowCountText;
        GoToPageAsync = goToPageAsync;
        SetPageSizeAsync = setPageSizeAsync;
    }

    public int CurrentPage { get; }
    public int TotalPages { get; }
    public int PageSize { get; }
    public int TotalRecords { get; }
    public IReadOnlyList<int> PageSizes { get; }
    public IReadOnlyList<int> PageNumbers { get; }
    public string RowCountText { get; }
    public Func<int, Task> GoToPageAsync { get; }
    public Func<int, Task> SetPageSizeAsync { get; }
}

/// <summary>Context supplied to a full-row template.</summary>
public sealed class GridRowTemplateContext<TValue>
{
    internal GridRowTemplateContext(TValue item, int rowIndex, bool selected, bool expanded)
    {
        Item = item;
        RowIndex = rowIndex;
        IsSelected = selected;
        IsExpanded = expanded;
    }

    public TValue Item { get; }
    public int RowIndex { get; }
    public bool IsSelected { get; }
    public bool IsExpanded { get; }
}

/// <summary>Context supplied when a host replaces the built-in column chooser body.</summary>
public sealed class GridColumnChooserContext
{
    internal GridColumnChooserContext(
        IReadOnlyList<GridColumnChooserItem> columns,
        Action<string> select,
        Action<string, bool> setVisible)
    {
        Columns = columns;
        Select = select;
        SetVisible = setVisible;
    }

    public IReadOnlyList<GridColumnChooserItem> Columns { get; }
    public Action<string> Select { get; }
    public Action<string, bool> SetVisible { get; }
}

public sealed record GridColumnChooserItem(
    string Field,
    string Header,
    bool Visible,
    bool CanHide,
    bool Selected);

