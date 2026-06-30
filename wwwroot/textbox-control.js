export function focus(element, selectText) {
    if (!element) return;
    requestAnimationFrame(() => {
        try {
            element.focus({ preventScroll: true });
            if (selectText && typeof element.select === "function")
                element.select();
        } catch {
            // Best effort only. Inputs can disappear while dialogs close.
        }
    });
}

export function select(element) {
    if (!element || typeof element.select !== "function") return;
    requestAnimationFrame(() => {
        try { element.select(); } catch { }
    });
}
