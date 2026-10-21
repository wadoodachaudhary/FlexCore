using Fx.ControlKit.Spreadsheet;

internal static class DocumentChecks
{
    public static void Run(Action<bool, string> check)
    {
        using var document = new SpreadsheetDocument();
        document.SetRange(new(), "Item\tRegion\tQty\nWidget\tNorth\t2\nService\tSouth\t3\nWidget extra\tnorth\t4\nWidget\tSouth\t5");
        var range = new SpreadsheetSelection(1, 1, 5, 3);
        document.Freeze(1, 1);
        check(document.FrozenRows == 1 && document.FrozenColumns == 1, "freeze records both axes");
        document.ApplyTextFilter(range, 2, "North");
        check(!document.Sheet.Row(2).IsHidden && document.Sheet.Row(3).IsHidden && !document.Sheet.Row(4).IsHidden, "text filter ignores case and hides nonmatching rows");
        document.ApplyTextFilter(range, 1, "Widget", SpreadsheetTextFilter.Equals);
        check(!document.Sheet.Row(2).IsHidden && document.Sheet.Row(4).IsHidden && document.Sheet.Row(5).IsHidden, "separate column filters combine with AND");
        document.ClearFilter(1);
        check(!document.Sheet.Row(4).IsHidden && document.Sheet.Row(3).IsHidden, "clear one column preserves the other filter");
        document.SetCell(2, 2, "South");
        check(document.Sheet.Row(2).IsHidden, "editing a filtered cell reapplies the filter");
        document.Undo();
        check(!document.Sheet.Row(2).IsHidden && document.Sheet.Cell(2, 2).GetString() == "North", "undo restores cell and row visibility together");
        using (var restored = new SpreadsheetDocument(document.Save()))
        {
            check(restored.FrozenRows == 1 && restored.FrozenColumns == 1 && restored.Sheet.AutoFilter.IsEnabled && restored.Sheet.Row(3).IsHidden, "XLSX round trip retains freeze and filter state");
            restored.ReapplyFilters();
            check(!restored.Sheet.Row(4).IsHidden && restored.Sheet.Row(3).IsHidden, "imported filter criteria reapply");
        }
        try { document.ApplyTextFilter(new(1, 1, 4, 3), 2, "South"); throw new Exception("Changed range was accepted"); }
        catch (InvalidOperationException) { check(!document.Sheet.Row(2).IsHidden && document.Sheet.Row(3).IsHidden, "changing an active filter range rejects and preserves previous state"); }
        document.ClearFilters();
        check(!document.Sheet.AutoFilter.IsEnabled && Enumerable.Range(1, 5).All(r => !document.Sheet.Row(r).IsHidden), "clear filters restores every filtered row");
        document.Undo();
        check(document.Sheet.AutoFilter.IsEnabled && document.Sheet.Row(3).IsHidden, "undo clear restores filter criteria and hidden rows");
        document.Freeze(0, 0);
        check(document.FrozenRows == 0 && document.FrozenColumns == 0, "unfreeze clears both axes");
        document.Sheet.Protect("test");
        try { document.ApplyTextFilter(range, 2, "South"); throw new Exception("Protected filter was accepted"); }
        catch (InvalidOperationException) { check(document.Sheet.IsProtected && document.Sheet.Row(3).IsHidden, "protected worksheet rejects filter mutation atomically"); }
        using var literals = new SpreadsheetDocument();
        literals.SetRange(new(), "Text\na*b\nab\nalpha\nALPHA\nalphabet");
        var literalRange = new SpreadsheetSelection(1, 1, 6, 1);
        literals.ApplyTextFilter(literalRange, 1, "*");
        check(!literals.Sheet.Row(2).IsHidden && literals.Sheet.Row(3).IsHidden, "contains treats wildcard characters literally");
        literals.ApplyTextFilter(literalRange, 1, "*", SpreadsheetTextFilter.NotContains);
        check(literals.Sheet.Row(2).IsHidden && !literals.Sheet.Row(3).IsHidden, "not-contains negates literal matching");
        literals.ClearFilters();
        literals.ApplyTextFilter(literalRange, 1, "alpha", SpreadsheetTextFilter.Equals);
        check(!literals.Sheet.Row(4).IsHidden && !literals.Sheet.Row(5).IsHidden && literals.Sheet.Row(6).IsHidden, "new exact filter after clear ignores case but excludes longer values");
        using var again = new SpreadsheetDocument(literals.Save());
        check(again.Sheet.AutoFilter.IsEnabled && again.Sheet.Row(6).IsHidden, "cleared and reapplied filters remain serializable");
        using var formulas = new SpreadsheetDocument();
        formulas.SetRange(new(), "Text\nalpha\n=A2");
        formulas.ApplyTextFilter(new(1, 1, 3, 1), 1, "alpha");
        check(!formulas.Sheet.Row(3).IsHidden, "text filters evaluate formula cells before matching");
        formulas.SetCell(2, 1, "beta");
        check(formulas.Sheet.Row(2).IsHidden && formulas.Sheet.Row(3).IsHidden, "dependent formula edits update filter visibility");
    }
}
