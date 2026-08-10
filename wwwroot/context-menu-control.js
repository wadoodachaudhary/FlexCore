export function positionContextMenu(menuElement, x, y, minWidth, zIndex) {
    if (!menuElement) return;

    requestAnimationFrame(() => {
        const margin = 6;
        const viewportWidth = Math.max(document.documentElement.clientWidth || 0, window.innerWidth || 0);
        const viewportHeight = Math.max(document.documentElement.clientHeight || 0, window.innerHeight || 0);

        menuElement.style.minWidth = `${Math.max(0, minWidth || 0)}px`;
        menuElement.style.zIndex = `${zIndex || 10000}`;
        menuElement.style.left = `${x}px`;
        menuElement.style.top = `${y}px`;
        menuElement.style.maxHeight = "";
        menuElement.style.overflowY = "visible";

        let rect = menuElement.getBoundingClientRect();

        if (rect.height > viewportHeight - margin * 2) {
            menuElement.style.maxHeight = `${Math.max(40, viewportHeight - margin * 2)}px`;
            menuElement.style.overflowY = "auto";
            rect = menuElement.getBoundingClientRect();
        }

        let left = Number.isFinite(x) ? x : margin;
        let top = Number.isFinite(y) ? y : margin;

        if (left + rect.width > viewportWidth - margin) {
            left = Math.max(margin, viewportWidth - margin - rect.width);
        }

        if (top + rect.height > viewportHeight - margin) {
            top = Math.max(margin, viewportHeight - margin - rect.height);
        }

        menuElement.style.left = `${Math.round(left)}px`;
        menuElement.style.top = `${Math.round(top)}px`;
    });
}

/**
 * Roving focus for a popup menu. VB6 used native Win32 PopupMenu, where the OS
 * supplied first-item highlight, Up/Down and Enter for free; these menus are plain
 * <button>s in a div, so the movement has to be driven explicitly. Enter/Space are
 * deliberately NOT handled here — a focused <button> already activates on both.
 *
 * mode: "first" | "last" | "next" | "prev". Returns true when focus moved.
 */
export function focusMenuItem(menuEl, mode) {
    if (!menuEl) return false;
    const items = [...menuEl.querySelectorAll("button")].filter(
        b => !b.disabled && b.offsetParent !== null);
    if (!items.length) return false;

    const current = items.indexOf(document.activeElement);
    let next;
    if (mode === "first") next = 0;
    else if (mode === "last") next = items.length - 1;
    else if (mode === "prev") next = current <= 0 ? items.length - 1 : current - 1;
    else next = current < 0 || current === items.length - 1 ? 0 : current + 1;

    items[next].focus();
    return true;
}

/**
 * Enter/Space on a menu item. The keydown handler prevents the browser default so
 * the first keystroke after the menu opens can never be swallowed by Blazor's
 * render-time :preventDefault evaluation, which means activation has to be driven
 * explicitly rather than left to the <button>.
 */
export function activateMenuItem(menuEl) {
    if (!menuEl) return false;
    const a = document.activeElement;
    if (a && menuEl.contains(a) && !a.disabled) { a.click(); return true; }
    return false;
}
