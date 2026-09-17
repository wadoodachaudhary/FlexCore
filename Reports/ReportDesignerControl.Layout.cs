using Microsoft.AspNetCore.Components.Web;

namespace Fx.ControlKit.Reports;

public partial class ReportDesignerControl
{
    private readonly HashSet<string> _selectedElementIds = new(StringComparer.Ordinal);
    private List<ReportDesignerElement> _clipboardElements = [];
    private int _splitAtTwips = 360;
    private string _sectionError = "";
    private string _moveSectionId = "";
    private List<ReportDesignerElement> SelectedObjects => _selectedElementIds.Count > 0
        ? ReportDesignerEditing.Selection(_document, _selectedElementIds) : _selectedElement is null ? [] : [_selectedElement];
    private async Task FormatSelectionAsync(Action<ReportDesignerElement> change, bool allowLocked = false)
    {
        var selected = SelectedObjects;
        if (!CanFormatSelection(allowLocked)) return;
        foreach (var element in selected) change(element);
        await MarkDirtyAsync();
    }
    private bool CanFormatSelection(bool allowLocked = false)
    {
        var selected = SelectedObjects;
        if (selected.Count == 0) return false;
        if (selected.Any(e => (!allowLocked && e.LockFormat) || _document.Sections.First(s => s.Id == e.SectionId).ReadOnly))
        { _status = "The selection contains a format-locked object or a read-only section."; return false; }
        return true;
    }
    private static readonly (string Command, string Icon, string Title, int Minimum)[] LayoutTools =
    [
        ("arrange-left", "bi bi-align-start", "Align object left edges", 2),
        ("arrange-center", "bi bi-align-center", "Align object horizontal centers", 2),
        ("arrange-right", "bi bi-align-end", "Align object right edges", 2),
        ("arrange-top", "bi bi-align-top", "Align object top edges", 2),
        ("arrange-middle", "bi bi-align-middle", "Align object vertical centers", 2),
        ("arrange-bottom", "bi bi-align-bottom", "Align object bottom edges", 2),
        ("arrange-distribute-x", "bi bi-distribute-horizontal", "Distribute horizontally", 3),
        ("arrange-distribute-y", "bi bi-distribute-vertical", "Distribute vertically", 3),
        ("arrange-width", "bi bi-arrows", "Match widths", 2),
        ("arrange-height", "bi bi-arrows-vertical", "Match heights", 2),
        ("arrange-size", "bi bi-aspect-ratio", "Match sizes", 2),
        ("layer-front", "bi bi-front", "Bring to front", 1),
        ("layer-back", "bi bi-back", "Send to back", 1),
        ("layer-forward", "bi bi-layer-forward", "Bring forward", 1),
        ("layer-backward", "bi bi-layer-backward", "Send backward", 1),
        ("duplicate", "bi bi-copy", "Duplicate selection", 1)
    ];

    private async Task ArrangeSelectionAsync(string command)
    {
        try
        {
            var ids = SelectedObjects.Select(e => e.Id).ToArray();
            if (command.StartsWith("layer-", StringComparison.Ordinal)) ReportDesignerEditing.ChangeLayer(_document, ids, command[6..]);
            else ReportDesignerEditing.Arrange(_document, ids, _selectedElement?.Id ?? "", command);
            await MarkDirtyAsync();
        }
        catch (InvalidOperationException ex) { _status = ex.Message; }
    }

    private async Task MoveObjectsToSectionAsync(string? id)
    {
        var destination = _document.Sections.FirstOrDefault(s => s.Id == id);
        if (destination is null) return;
        try
        {
            var selected = SelectedObjects;
            ReportDesignerEditing.MoveToSection(_document, selected.Select(e => e.Id), destination);
            _selectedSection = destination; _moveSectionId = destination.Id;
            await MarkDirtyAsync();
        }
        catch (InvalidOperationException ex) { _status = ex.Message; }
    }

    private void CopyObjects()
    {
        _clipboardElements = SelectedObjects.Select(CloneElementForClipboard).ToList();
        _clipboardElement = _clipboardElements.FirstOrDefault();
        _status = $"Copied {_clipboardElements.Count} object(s).";
    }

    private async Task PasteObjectsAsync(bool duplicate = false)
    {
        var originals = duplicate ? SelectedObjects : _clipboardElements.Count > 0 ? _clipboardElements : _clipboardElement is null ? [] : new List<ReportDesignerElement> { _clipboardElement };
        if (originals.Count == 0) return;
        var destination = GetActiveSection();
        if (destination.ReadOnly) { _status = "The destination section is read-only."; return; }
        var left = originals.Min(e => e.LeftTwips); var right = originals.Max(e => e.LeftTwips + e.WidthTwips);
        if (right - left > _document.Page.ContentWidthTwips) { _status = "The selection is wider than the printable page."; return; }
        var offset = Math.Clamp(180, -left, _document.Page.ContentWidthTwips - right);
        var pasted = new List<ReportDesignerElement>();
        foreach (var source in originals)
        {
            var section = duplicate ? _document.Sections.First(s => s.Elements.Contains(source)) : destination;
            if (section.ReadOnly) { _status = "The selection contains a read-only section."; return; }
        }
        foreach (var source in originals)
        {
            var section = duplicate ? _document.Sections.First(s => s.Elements.Contains(source)) : destination;
            var clone = source.CloneFor(section.Id, NextObjectName(source.Name));
            clone.LeftTwips = source.LeftTwips + offset;
            section.Elements.Add(clone); section.HeightTwips = Math.Max(section.HeightTwips, clone.TopTwips + clone.HeightTwips);
            pasted.Add(clone);
        }
        SelectElement(new ReportDesignerElementSelection { Section = _document.Sections.First(s => s.Id == pasted[^1].SectionId), Element = pasted[^1], Elements = pasted });
        await MarkDirtyAsync();
    }

    private async Task DeleteObjectsAsync(bool cut = false)
    {
        var selected = SelectedObjects;
        if (selected.Count == 0) return;
        try { ReportDesignerEditing.RequireEditable(_document, selected); }
        catch (InvalidOperationException ex) { _status = ex.Message; return; }
        if (cut) CopyObjects();
        foreach (var section in _document.Sections) section.Elements.RemoveAll(selected.Contains);
        _selectedElementIds.Clear(); _selectedElement = null;
        await MarkDirtyAsync();
    }

    private Task OnCanvasElementsChangedAsync(ReportDesignerSection section) => MarkDirtyAsync();

    private Task SectionCommandAsync(string command)
    {
        if (ActiveExpertSection is not { } section) return Task.CompletedTask;
        var draft = new ReportDesignerDocument { Sections = _expertSections, Page = _document.Page };
        foreach (var source in _document.SourceSections) draft.SourceSections[source.Key] = source.Value;
        _sectionError = "";
        try
        {
            switch (command)
            {
                case "insert": section = ReportDesignerEditing.InsertSection(draft, section); break;
                case "delete": ReportDesignerEditing.DeleteSection(draft, section); break;
                case "up": ReportDesignerEditing.MoveSection(draft, section, -1); break;
                case "down": ReportDesignerEditing.MoveSection(draft, section, 1); break;
                case "split": section = ReportDesignerEditing.SplitSection(draft, section, _splitAtTwips); break;
                case "merge": ReportDesignerEditing.MergeSection(draft, section); break;
                case "fit": ReportDesignerEditing.FitSection(draft, section); break;
            }
            _activeExpertItemIndex = Math.Clamp(_expertSections.IndexOf(section), 0, Math.Max(0, _expertSections.Count - 1));
        }
        catch (InvalidOperationException ex) { _sectionError = ex.Message; }
        return Task.CompletedTask;
    }
}
