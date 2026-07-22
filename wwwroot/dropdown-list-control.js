export function measureDropdown(host, desiredMaxHeight = 180, margin = 8) {
    if (!host) {
        return { openUp: false, maxHeight: desiredMaxHeight, top: margin, left: margin, minWidth: 0 };
    }

    const rect = host.getBoundingClientRect();
    const panel = host.querySelector(".fx-dropdown-panel");
    const viewportWidth = window.innerWidth || document.documentElement.clientWidth || 0;
    const viewportHeight = window.innerHeight || document.documentElement.clientHeight || 0;
    const panelHeight = panel
        ? panel.scrollHeight || panel.offsetHeight || desiredMaxHeight
        : desiredMaxHeight;
    const desiredHeight = Math.max(1, Math.min(panelHeight, desiredMaxHeight));
    const spaceBelow = Math.max(0, viewportHeight - rect.bottom - margin);
    const spaceAbove = Math.max(0, rect.top - margin);
    const openUp = spaceBelow < desiredHeight && spaceAbove > spaceBelow;
    const available = openUp ? spaceAbove : spaceBelow;
    const maxHeight = Math.max(36, Math.min(desiredHeight, available || desiredHeight));
    const minWidth = Math.max(0, rect.width);
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
        minWidth
    };
}
