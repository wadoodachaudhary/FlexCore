export function measureDropdown(host, desiredMaxHeight = 180, margin = 8) {
    if (!host) {
        return { openUp: false, maxHeight: desiredMaxHeight, top: margin, left: margin, minWidth: 0 };
    }

    const rect = host.getBoundingClientRect();
    const panel = host.querySelector(".fx-dropdown-panel");
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
    const panelWidth = minWidth;
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

