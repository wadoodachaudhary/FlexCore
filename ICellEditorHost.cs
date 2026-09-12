using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Fx.ControlKit;

/// <summary>
/// A cell that hosts an in-cell editor (a TreeGridControl cell-edit template) cascades ONE
/// of these to the editor it renders. FlexKit editors take it as a cascading parameter
/// and wire themselves: the page declares only the editor and its data binding.
/// </summary>
/// <remarks>
/// A hosted cell has two phases, the vsFlexGrid model (Editable=flexEDKbdMouse with
/// ShowComboButton on):
/// <list type="bullet">
/// <item><b>Cursor</b> (<see cref="IsEditing"/> false): the cell is current. A popup editor
/// (list, calendar) renders its CLOSED form so the affordance is visible, but takes no
/// focus, no Tab stop and no mouse — the host owns the keyboard and the mouse. A text
/// editor renders nothing; the cell's display shows.</item>
/// <item><b>Editing</b> (<see cref="IsEditing"/> true): the editor takes focus, opens its
/// popup when <see cref="OpenOnRender"/> asks, starts from <see cref="InitialText"/> when
/// typing began the edit, and reports back: keys it does not consume go to
/// <see cref="KeyDownAsync"/>, and every commit / cancel / close ends in <see cref="EndEdit"/>.</item>
/// </list>
/// Each phase change is a NEW context and a fresh editor instance (the host keys the editor
/// on <see cref="Generation"/>), so first-render facts stay first-render facts. A context
/// that is no longer the host's current one ignores every call.
/// </remarks>
public interface ICellEditorHost
{
    /// <summary>True while the cell is being edited; false in the cursor phase.</summary>
    bool IsEditing { get; }

    /// <summary>Editing phase: open the list / calendar as soon as the editor mounts
    /// (Enter, or a second click on the current cell). A date editor honours it only
    /// when its value is blank.</summary>
    bool OpenOnRender { get; }

    /// <summary>Editing phase started by typing: the character(s) typed. A text editor
    /// REPLACES its value with them; a list highlights the first match.</summary>
    string? InitialText { get; }

    /// <summary>Changes with every phase change; the host keys the editor on it.</summary>
    int Generation { get; }

    /// <summary>The edit ended — the value was committed, the entry cancelled, or the
    /// popup closed. The host returns the cell to the cursor phase and takes the
    /// keyboard back unless focus has already moved elsewhere on the page.</summary>
    void EndEdit();

    /// <summary>Keys the editor yields to the host: Enter commits and stays on the
    /// cell, Escape leaves it, Tab / Shift+Tab leave and move to the next / previous
    /// cell, Up / Down leave and move the row, Right / Left leave and act on the
    /// outline (VB6 gProperties: expand / first child, collapse / parent; a Cell-mode
    /// host moves the cell cursor). Which keys an editor yields is the editor's
    /// decision — a text editor keeps Left / Right / Up / Down for its caret and
    /// yields only Enter, Escape and Tab; a closed list yields the arrows too; an open
    /// popup (list, calendar) keeps its own navigation keys. At the first / last cell
    /// of a property grid Tab belongs to the page (the host's edge attribute lets the
    /// page graph take it before the editor ever sees it).</summary>
    Task KeyDownAsync(KeyboardEventArgs e);

    /// <summary>Focuses the editor's element — unless focus has already moved on to
    /// something outside the host while the editor was mounting (a Tab at the last
    /// cell, a click elsewhere): an editor never pulls the keyboard back, and the
    /// edit then ends as if focus had left it. Hosted editors route their own
    /// focus steps (autofocus, the focus hand-over to an open list) through this.</summary>
    Task FocusAsync(ElementReference element, bool selectText = false);

    /// <summary>The editing editor registers the handler that receives every key that
    /// reaches the host while the editor is mounting or has not taken focus yet
    /// (printable characters, Enter, Escape, Up/Down, F4, Alt+Down) — nothing typed in
    /// the round trips before focus lands is lost. Register in <c>OnInitialized</c>
    /// (inside the mount render, before any key can arrive); pass null to detach.</summary>
    void AttachKeyRelay(Func<KeyboardEventArgs, Task>? relay);
}
