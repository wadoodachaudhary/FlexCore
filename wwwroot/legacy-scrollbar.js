/* FlexKit — browser-side geometry for the opt-in legacy (Win9x-style) scrollbar.
 *
 * Loaded on demand by TreeGridControl when ShowLegacyScrollBar is set:
 *   _scrollJsModule = await JsRuntime.InvokeAsync<IJSObjectReference>(
 *       "import", LegacyScrollBarJsModulePath);
 *
 * WHY THIS FILE EXISTS AT ALL
 * A browser-drawn scrollbar swallows every mouse event — clicking one dispatches
 * no mousedown and no contextmenu to the page — so a host can never put a menu on
 * it, and it cannot be styled to match a VB6 form. Drawing the bar as ordinary DOM
 * fixes both, but then the component has to know scrollTop / scrollHeight /
 * clientHeight, and Blazor cannot read layout. These three readers are the whole
 * reason for the interop; everything else (thumb size, drag maths, paging) stays
 * in C#.
 *
 * Every function degrades to a harmless value when the element is gone, so a torn
 * -down circuit or a disposed grid can never throw here — the C# side treats any
 * failure as "hide the scrollbar", and wheel/keyboard scrolling still works.
 */

/**
 * Reads the three numbers that define a scroller's state.
 * @param {Element} el the scrolling element
 * @returns {number[]} [scrollTop, scrollHeight, clientHeight]; all zero when absent.
 */
export function readScrollMetrics(el) {
    if (!el) return [0, 0, 0];
    return [el.scrollTop, el.scrollHeight, el.clientHeight];
}

/**
 * Sets a scroller's position, clamped to the legal range so the caller never has
 * to know the bounds (page-up from row 2, "Bottom" from anywhere, etc.).
 * @param {Element} el the scrolling element
 * @param {number} top desired scrollTop
 */
export function setScrollTop(el, top) {
    if (!el) return;
    const max = Math.max(0, el.scrollHeight - el.clientHeight);
    el.scrollTop = Math.max(0, Math.min(max, top));
}

/**
 * Viewport-space rectangle of an element, in the same coordinate space as
 * MouseEventArgs.ClientX/ClientY so the two can be compared directly in C#.
 * @param {Element} el
 * @returns {number[]} [left, top, width, height]
 */
export function readElementRect(el) {
    if (!el || !el.getBoundingClientRect) return [0, 0, 0, 0];
    const r = el.getBoundingClientRect();
    return [r.left, r.top, r.width, r.height];
}

const keyboardNavigationRoots = new WeakSet();

/**
 * Stops the browser's own line/page scrolling when a TreeGrid row owns focus.
 * Blazor still receives the key and performs the matching tree navigation.
 * Events from editors and buttons are left untouched.
 * @param {Element} root the TreeGrid root element
 */
export function enableTreeKeyboardNavigation(root, scroller, dotNetRef) {
    if (!root || keyboardNavigationRoots.has(root)) return;

    // Focus leaving the tree while a cell is being edited (a page-level Tab, a
    // click elsewhere) ends the edit; the server ignores a stale generation, so
    // the focusout Chrome fires when a finished editor is REMOVED is harmless.
    // Checked after the browser has settled the new focus: a move inside the
    // tree (into the editor or its popup) is not a leave, and a window that
    // lost focus (alt-tab) keeps its edit, as VB6 does.
    if (dotNetRef) {
        root.addEventListener("focusout", () => {
            const generation = root.dataset.fxCellEditing;
            if (generation === undefined) return;
            setTimeout(() => {
                if (root.dataset.fxCellEditing !== generation) return;
                const doc = root.ownerDocument;
                if (!doc.hasFocus()) return;
                const active = doc.activeElement;
                if (active && active !== root && root.contains(active)) return;
                dotNetRef.invokeMethodAsync("OnCellEditFocusLeftAsync", Number(generation)).catch(() => { });
            }, 0);
        });
    }

    root.addEventListener("keydown", event => {
        const target = event.target;
        if (event.key === "Tab" && target?.closest?.(".fx-treegrid-batch-editor")) { event.preventDefault(); return; }
        const onRootOrRow = target === root || !!target?.classList?.contains("fx-treegrid-row");
        const inEditHost = !!target?.closest?.(".fx-treegrid-cell-edit-host");
        const onDisplay = !!target?.classList?.contains("fx-treegrid-cell-edit-display");

        if (event.key === "Tab") {
            // A property grid (wrap-until-edge) walks its rows on Tab: the browser's own
            // focus move must not happen unless the edge attribute releases the key in
            // that direction — the page graph's test (page-control.js), mirrored. Other
            // trees are one page stop and keep the native / graph Tab.
            if (root.dataset.fxGridTabNavigation !== "wrap-until-edge" || event.altKey || event.ctrlKey || event.metaKey) return;
            const edge = root.dataset.fxGridTabEdge ?? "none";
            const releases = edge === "both" || (event.shiftKey ? edge === "first" : edge === "last");
            const inScope = onRootOrRow || onDisplay || (inEditHost && !target.closest("[data-fx-key-scope]"));
            if (!releases && inScope) event.preventDefault();
            return;
        }

        if (["ArrowDown", "ArrowUp", "ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) {
            // Rows, the root and the non-text parts of a cell editor (a list's button
            // and options, a display button): the browser would scroll the pane. A
            // text input keeps its caret keys (Up / Down = start / end of the text).
            const textTarget = target instanceof HTMLTextAreaElement
                || (target instanceof HTMLInputElement && !target.readOnly
                    && !["button", "checkbox", "radio", "submit", "reset"].includes(target.type));
            if ((onRootOrRow || onDisplay || inEditHost) && !textTarget) event.preventDefault();
            return;
        }

        // Space toggles the current folder (VB6): never page-scroll as well.
        if (event.key === " " && onRootOrRow) event.preventDefault();
    }, { capture: true });

    root.addEventListener("dragstart", event => {
        const row = event.target.closest?.("tr[draggable='true']");
        if (row && event.dataTransfer) { event.dataTransfer.setData("text/plain", row.dataset.nodeId); event.dataTransfer.effectAllowed = "move"; }
    });
    keyboardNavigationRoots.add(root);
}

/**
 * Focuses a selected row without browser recentering, then moves only the
 * TreeGrid viewport by the amount needed to keep that row visible.
 * @param {Element} scroller the TreeGrid scrolling viewport
 * @param {Element} row the newly selected row
 */
export function focusTreeRow(scroller, row) {
    if (!row) return;
    // A keyboard row move never pulls focus back from another page control (a Tab
    // or a click that landed while the move was rendering) — but the row it moved
    // to is still scrolled into view: the selection moved either way.
    if (treeOwnsFocus(row.closest("[data-fx-tab-stop]") ?? scroller))
        row.focus({ preventScroll: true });
    if (!scroller || !scroller.getBoundingClientRect) return;

    const viewportRect = scroller.getBoundingClientRect();
    const rowRect = row.getBoundingClientRect();
    const visibilityInset = 1;

    if (rowRect.top < viewportRect.top + visibilityInset) {
        scroller.scrollTop -= viewportRect.top + visibilityInset - rowRect.top;
    } else if (rowRect.bottom > viewportRect.bottom - visibilityInset) {
        scroller.scrollTop += rowRect.bottom - viewportRect.bottom + visibilityInset;
    }
}

/**
 * Focuses the row selected in the current render of a TreeGrid.
 * @param {Element} scroller the TreeGrid scrolling viewport
 */
export function focusSelectedTreeRow(scroller) {
    const row = scroller?.querySelector("tr.fx-treegrid-selected");
    focusTreeRow(scroller, row);
}

// CSS provides sticky positioning; the DOM supplies actual widths after resizing.
const treeGridLayouts = new WeakMap();
export function syncTreeGridLayout(root) {
    if (!root) return;
    let layout = treeGridLayouts.get(root);
    if (!layout) {
        const update = () => {
            root.style.setProperty("--fx-tree-toolbar-height", `${root.querySelector(".fx-treegrid-edit-toolbar")?.getBoundingClientRect().height || 0}px`);
            const cells = [...root.querySelectorAll("thead th[data-tree-column]")];
            for (const side of ["left", "right"]) {
                let offset = side === "left" ? (root.querySelector("thead .fx-treegrid-check-cell")?.getBoundingClientRect().width || 0) : 0;
                for (const cell of side === "left" ? cells : [...cells].reverse()) {
                    if (cell.dataset.treeFrozen !== side) continue;
                    root.style.setProperty(`--fx-tree-col-${cell.dataset.treeColumn}`, `${offset}px`);
                    offset += cell.getBoundingClientRect().width;
                }
            }
        };
        const observer = new ResizeObserver(update);
        layout = { observer, update, cells: [] }; treeGridLayouts.set(root, layout);
    }
    layout.observer.disconnect(); layout.observer.observe(root);
    for (const cell of root.querySelectorAll("thead th")) layout.observer.observe(cell);
    layout.update();
}
export function disposeTreeGridLayout(root) {
    treeGridLayouts.get(root)?.observer.disconnect(); treeGridLayouts.delete(root);
}

/**
 * Returns keyboard focus to a TreeGrid after its in-cell editor closed — only
 * when focus is still inside the tree or was lost with the editor (body). An
 * editor that closes on blur after a page-level Tab must not pull focus back.
 * @param {Element} root the TreeGrid root (tabindex=0)
 */
export function focusTreeIfFocusLost(root) {
    if (!root) return;
    if (treeOwnsFocus(root))
        root.focus({ preventScroll: true });
}

// Focus is still the tree's to place: inside it, or lost (body / nothing).
function treeOwnsFocus(root) {
    if (!root) return true;
    const doc = root.ownerDocument;
    const active = doc.activeElement;
    return !active || active === doc.body || active === doc.documentElement || root.contains(active);
}

/**
 * A hosted cell editor's focus step (autofocus, the hand-over to an open list):
 * focuses the element only while the tree still owns the keyboard — never after
 * focus moved on to another control while the editor was mounting. Returns
 * whether the element was focused (false: the host ends the edit).
 * @param {Element} root the TreeGrid root
 * @param {HTMLElement} element the editor element
 * @param {boolean} selectText select the text as it is focused
 */
export function focusIfTreeOwnsFocus(root, element, selectText) {
    // Focus moved on to another control: the caller ends the edit.
    if (!treeOwnsFocus(root)) return false;
    // Still the tree's keyboard, but this element is gone (a re-render replaced it):
    // there is nothing to focus and nothing to end.
    if (!element || !element.isConnected) return true;
    try {
        element.focus({ preventScroll: true });
        if (selectText && typeof element.select === "function") element.select();
    } catch { }
    return true;
}
