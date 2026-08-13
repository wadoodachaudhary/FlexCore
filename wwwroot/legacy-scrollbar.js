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
export function enableTreeKeyboardNavigation(root) {
    if (!root || keyboardNavigationRoots.has(root)) return;

    root.addEventListener("keydown", event => {
        const target = event.target;
        if (target !== root && !target?.classList?.contains("fx-treegrid-row")) return;

        if (["ArrowDown", "ArrowUp", "ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) {
            event.preventDefault();
        }
    }, { capture: true });

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
