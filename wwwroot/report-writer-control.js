/* FlexKit — browser-side helpers for ReportWriterControl.
 *
 * Kept as a lazy-imported ES module so report interaction stays owned by the
 * control library instead of requiring host apps to register global helpers.
 */

const drillBindings = new WeakMap();

function getHost(root) {
    return root || document;
}

function getDocument(root) {
    return (root && root.ownerDocument) || document;
}

function waitForElement(root, anchorId, callback, attempts) {
    if (!anchorId) {
        callback(null);
        return;
    }

    if (typeof attempts !== "number") attempts = 0;

    const host = getHost(root);
    const doc = getDocument(root);
    const escaped = typeof CSS !== "undefined" && CSS.escape
        ? CSS.escape(anchorId)
        : anchorId.replace(/(["\\])/g, "\\$1");
    const el = host.querySelector
        ? host.querySelector(`#${escaped}`)
        : doc.getElementById(anchorId);

    if (el) {
        callback(el);
        return;
    }

    if (attempts >= 33) {
        callback(null);
        return;
    }

    const win = doc.defaultView || window;
    win.setTimeout(() => waitForElement(root, anchorId, callback, attempts + 1), 30);
}

export function attachDrillHandler(root, dotNetRef) {
    const host = getHost(root);
    detachDrillHandler(root);

    const handler = (event) => {
        const target = event.target instanceof Element ? event.target : null;
        if (!target) return;

        const subreportLink = target.closest("a.fx-rpt-subreport-link");
        if (subreportLink) {
            const payload = subreportLink.getAttribute("data-subreport");
            if (payload) {
                dotNetRef.invokeMethodAsync("OnSubreportClick", payload);
                event.preventDefault();
                event.stopPropagation();
            }
            return;
        }

        const row = target.closest(".fx-drill-row");
        if (!row || (root && !root.contains(row))) return;

        const path = row.getAttribute("data-path");
        if (!path) return;

        row.style.transition = "background-color 0.4s ease";
        const previousBackground = row.style.backgroundColor;
        row.style.backgroundColor = "#ffeb3b";
        const win = getDocument(root).defaultView || window;
        win.setTimeout(() => { row.style.backgroundColor = previousBackground || ""; }, 500);

        dotNetRef.invokeMethodAsync("OnCellClick", path);
        event.preventDefault();
        event.stopPropagation();
    };

    host.addEventListener("click", handler, true);
    drillBindings.set(host, handler);
}

export function detachDrillHandler(root) {
    const host = getHost(root);
    const handler = drillBindings.get(host);
    if (!handler) return;

    host.removeEventListener("click", handler, true);
    drillBindings.delete(host);
}

export function scrollToAnchor(root, anchorId) {
    waitForElement(root, anchorId, (el) => {
        if (!el) return;

        el.scrollIntoView({ behavior: "smooth", block: "center" });
        const previousBackground = el.style.backgroundColor;
        el.style.transition = "background-color 0.4s ease";
        el.style.backgroundColor = "#ffeb3b";
        const win = getDocument(root).defaultView || window;
        win.setTimeout(() => { el.style.backgroundColor = previousBackground || ""; }, 1200);
    });
}

export function highlightGroup(root, anchorId) {
    clearGroupHighlight(root);
    waitForElement(root, anchorId, (row) => {
        if (!row) return;

        clearGroupHighlight(root);
        row.classList.add("fx-rpt-group-active");
        row.querySelectorAll(":scope > td")
            .forEach(td => td.classList.add("fx-rpt-group-active"));
        row.scrollIntoView({ behavior: "smooth", block: "center" });
    });
}

export function clearGroupHighlight(root) {
    const host = getHost(root);
    const previous = host.querySelectorAll
        ? host.querySelectorAll(".fx-rpt-group-active")
        : document.querySelectorAll(".fx-rpt-group-active");

    previous.forEach(el => el.classList.remove("fx-rpt-group-active"));
}
