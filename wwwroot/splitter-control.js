export function measureSplitter(rootEl, vertical) {
    if (!rootEl) return 0;
    const r = rootEl.getBoundingClientRect();
    return vertical ? r.width : r.height;
}
