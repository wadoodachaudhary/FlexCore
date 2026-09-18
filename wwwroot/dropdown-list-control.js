export function measureDropdown(host, desiredMaxHeight = 180, margin = 8, panelWidthOverride = 0, panelElement = null) {
    if (!host) {
        return { openUp: false, maxHeight: desiredMaxHeight, top: margin, left: margin, minWidth: 0 };
    }

    const rect = host.getBoundingClientRect();
    // Callers whose popup is not a .fx-dropdown-panel (DropDownGridControl) pass the
    // popup element so its REAL rendered height drives the flip and the placement.
    const panel = panelElement || host.querySelector(".fx-dropdown-panel");
    const viewportWidth = window.innerWidth || document.documentElement.clientWidth || 0;
    const viewportHeight = window.innerHeight || document.documentElement.clientHeight || 0;

    // HHM-576: the panel is position:absolute inside the host, so any scrollable
    // ancestor (computed overflow-y auto/scroll/hidden/clip/overlay) clips it.
    // Measuring the open-up flip against the viewport alone let the panel open
    // downward into a scroll container's overflow — the options merely extended
    // the container's scrollHeight and stayed hidden until the user scrolled.
    // Walk the clip ancestors and intersect their rects with the viewport, then
    // measure the space above/below against that visible rect so the panel flips
    // upward when the host sits at the bottom of a scrollable region (matching
    // the VB6 combo auto-flip behavior).
    let visibleTop = 0;
    let visibleBottom = viewportHeight;
    let ancestor = host.parentElement;
    while (ancestor && ancestor !== document.body && ancestor !== document.documentElement) {
        const overflowY = (window.getComputedStyle(ancestor).overflowY || "").toLowerCase();
        if (overflowY && overflowY !== "visible") {
            const ancestorRect = ancestor.getBoundingClientRect();
            visibleTop = Math.max(visibleTop, ancestorRect.top);
            visibleBottom = Math.min(visibleBottom, ancestorRect.bottom);
        }
        ancestor = ancestor.parentElement;
    }

    // Border-box math: max-height applies to the border box, but scrollHeight is
    // content-box. The old code set max-height = scrollHeight, leaving the panel
    // exactly its borders (2px) short of its own content — a permanent scrollbar
    // with the last option clipped on every list taller than one row.
    const cs = panel ? window.getComputedStyle(panel) : null;
    const borderY = cs ? (parseFloat(cs.borderTopWidth) || 0) + (parseFloat(cs.borderBottomWidth) || 0) : 0;
    const borderX = cs ? (parseFloat(cs.borderLeftWidth) || 0) + (parseFloat(cs.borderRightWidth) || 0) : 0;

    const options = panel ? panel.querySelectorAll(".fx-dropdown-option") : [];
    let optionHeight = 0;
    let maxOptionWidth = 0;
    for (const option of options) {
        if (!optionHeight) optionHeight = option.offsetHeight || 0;
        // Options are white-space:nowrap, so scrollWidth is the true text+padding
        // width even while the panel clips them.
        maxOptionWidth = Math.max(maxOptionWidth, option.scrollWidth || 0);
    }

    const contentHeight = panel
        ? panel.scrollHeight || panel.offsetHeight || desiredMaxHeight
        : desiredMaxHeight;
    const desiredHeight = Math.max(1, Math.min(contentHeight + borderY, desiredMaxHeight));
    const spaceBelow = Math.max(0, visibleBottom - rect.bottom - margin);
    const spaceAbove = Math.max(0, rect.top - visibleTop - margin);
    const openUp = spaceBelow < desiredHeight && spaceAbove > spaceBelow;
    const available = openUp ? spaceAbove : spaceBelow;
    let maxHeight = Math.max(36, Math.min(desiredHeight, available || desiredHeight));
    // When space clips the list, cut on a whole-option boundary instead of mid-row.
    if (optionHeight > 0 && maxHeight < contentHeight + borderY) {
        const rows = Math.max(1, Math.floor((maxHeight - borderY) / optionHeight));
        maxHeight = Math.max(36, rows * optionHeight + borderY);
    }
    const willScroll = contentHeight + borderY > maxHeight + 0.5;
    const minWidth = Math.max(0, rect.width);
    // Text-based width for hosts that opt in (grid cell editors): the widest
    // option plus panel chrome, plus the scrollbar lane when the list scrolls.
    const contentWidth = maxOptionWidth > 0
        ? Math.max(60, maxOptionWidth + borderX + (willScroll ? 14 : 0))
        : 0;
    // Fixed-position popups wider than their host (DropDownGridControl) pass their own
    // width so the right-edge clamp keeps the whole popup on screen.
    const panelWidth = panelWidthOverride > 0 ? panelWidthOverride : minWidth;
    const maxLeft = Math.max(margin, viewportWidth - margin - panelWidth);
    const left = Math.min(Math.max(margin, rect.left), maxLeft);
    const top = openUp
        ? Math.max(margin, rect.top - maxHeight + 1)
        : Math.min(Math.max(margin, rect.bottom - 1), Math.max(margin, viewportHeight - margin - maxHeight));

    return {
        openUp,
        maxHeight,
        top,
        left,
        minWidth,
        contentWidth
    };
}

// Keep layout, scroll, focus and the first visible paint in the same browser turn.
// The panel reference identifies this opening; a late call cannot reveal a replacement.
export function prepareDropdown(host, panel, editable, dotNetRef) {
    if (!host?.isConnected || !panel?.isConnected || !host.contains(panel)) return null;
    const geometry = measureDropdown(host, 180, 8, 0, panel);
    host.classList.toggle("fx-dropdown-open-up", geometry.openUp);
    panel.style.removeProperty("top");
    panel.style.maxHeight = `${geometry.maxHeight}px`;
    if (!panel.classList.contains("fx-dropdown-panel-fit")) {
        panel.style.width = panel.style.minWidth = panel.style.maxWidth = `${geometry.minWidth}px`;
    }
    const option = panel.querySelector(".fx-dropdown-option.highlighted");
    if (option) {
        // An item out of view opens as the TOP row, as the VB6 combo drops its list
        // (top index = current item); not the bottom row, where the next ArrowDown
        // would have to scroll.
        const top = option.offsetTop;
        const bottom = top + option.offsetHeight;
        if (top < panel.scrollTop || bottom > panel.scrollTop + panel.clientHeight)
            panel.scrollTop = Math.max(0, Math.min(top, panel.scrollHeight - panel.clientHeight));
        if (!editable) option.focus({ preventScroll: true });
    }
    if (!editable) watchFocusLeave(host, dotNetRef);
    panel.style.removeProperty("opacity");
    panel.style.removeProperty("pointer-events");
    host.querySelector(".fx-dropdown-backdrop")?.style.removeProperty("visibility");
    return geometry;
}


// Focus leaving an OPEN list: one registration per open, judged after the
// browser has settled the new focus — a move between the list's own options is
// not a leave, and a window that lost focus (alt-tab) keeps its list. The
// control decides what leaving means (commit the highlighted option / close).
const focusLeaveWatchers = new WeakMap();
export function watchFocusLeave(host, dotNetRef) {
    if (!host || !dotNetRef || focusLeaveWatchers.has(host)) return;
    const onFocusOut = () => {
        setTimeout(() => {
            if (!focusLeaveWatchers.has(host)) return;
            const doc = host.ownerDocument;
            if (!doc.hasFocus()) return;
            const active = doc.activeElement;
            if (active && host.contains(active)) return;
            dotNetRef.invokeMethodAsync("OnListFocusLeftAsync").catch(() => { });
        }, 0);
    };
    host.addEventListener("focusout", onFocusOut);
    focusLeaveWatchers.set(host, onFocusOut);
}
export function unwatchFocusLeave(host) {
    const onFocusOut = host && focusLeaveWatchers.get(host);
    if (!onFocusOut) return;
    host.removeEventListener("focusout", onFocusOut);
    focusLeaveWatchers.delete(host);
}
