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
