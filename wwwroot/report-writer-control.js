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

// Keyboard and wheel paging of the viewer, owned by the browser: PageUp/PageDown, Home/End and the wheel
// scroll the pane natively; only when the pane is already at its edge does the next step turn the page, one
// .NET call per turned page. Ctrl+Home / Ctrl+End go to the first / last page. The keys reach the visible
// viewer from the pane, from its toolbar and tab strip, from the MDI tab and from the page itself (nothing
// focused); keys typed into a field, a list, a grid, a tree, a splitter bar or a dialog stay there. A key turns the page at
// once. The wheel movement that reached the edge, and its tail, stay on the page; a fresh movement at the
// edge turns it, and the wheel rests for 600 ms after a turn, so a trackpad's momentum does not run through
// the report and the notches that turned the page do not scroll the new one away from its top.
// Several viewers can be shown at once (MDI split view): one keydown listener per document gives each key to one
// viewer at most (viewerForKey).
const navigationBindings = new WeakMap();
const keyRouters = new WeakMap();

const paneOf = (target) => target instanceof Element ? target.closest(".fx-rpt-viewer-panel") : null;
const ownsKeys = (target) => target instanceof Element && !!target.closest(
    "input, textarea, select, [contenteditable=true], [role=listbox], [role=combobox], [role=grid], [role=tree], [role=menu], [role=dialog], [role=separator], .fx-dialog, .fx-grid, .fx-treeview, .fx-treegrid, .fx-dropdown-list, .fx-context-menu");
const isShown = (element) => !!(element.getClientRects && element.getClientRects().length);

// The viewer a key is for. A key pressed in a viewer (its pane, toolbar or tab strip) is that viewer's. A key pressed
// on the page itself (nothing focused), on an MDI tab or on another element that takes no focus belongs to the shown
// viewer of the window it was pressed in: the MDI pane holding the target, else the active pane of a split view,
// else the first shown viewer. Another focusable element keeps its own keys.
function viewerForKey(doc, viewers, target) {
    if (ownsKeys(target)) return null;
    const shown = [];
    for (const viewer of viewers) if (isShown(viewer.host)) shown.push(viewer);
    // Document order, so the choice does not depend on which viewer attached first.
    shown.sort((a, b) => (a.host.compareDocumentPosition(b.host) & 4) ? -1 : 1);
    const element = target instanceof Element && target !== doc.body && target !== doc.documentElement ? target : null;
    if (element) {
        let holder = null;
        for (const viewer of shown) if (viewer.host.contains(element)) holder = viewer;
        if (holder) return holder;
        if (paneOf(element)) return null;
        const tab = element.closest(".fx-mdi-tab");
        if (!tab && element.hasAttribute("tabindex")) return null;
        const mdiPane = tab ? null : element.closest(".fx-mdi-pane");
        if (mdiPane) return shown.find((viewer) => mdiPane.contains(viewer.host)) || null;
    }
    const active = ((element && element.closest(".fx-mdi")) || doc).querySelector(".fx-mdi-pane-active");
    if (active) return shown.find((viewer) => active.contains(viewer.host)) || null;
    return shown[0] || null;
}

function routeKeys(doc, binding) {
    let router = keyRouters.get(doc);
    if (!router) {
        const viewers = new Set();
        const onKeyDown = (event) => {
            if (event.defaultPrevented || event.altKey || event.metaKey) return;
            const viewer = viewerForKey(doc, viewers, event.target);
            if (viewer) viewer.onKeyDown(event);
        };
        router = { viewers, onKeyDown };
        keyRouters.set(doc, router);
        doc.addEventListener("keydown", onKeyDown);
    }
    router.viewers.add(binding);
}

function unrouteKeys(doc, binding) {
    const router = keyRouters.get(doc);
    if (!router) return;
    router.viewers.delete(binding);
    if (router.viewers.size) return;
    doc.removeEventListener("keydown", router.onKeyDown);
    keyRouters.delete(doc);
}

export function attachViewerNavigation(root, dotNetRef) {
    const host = getHost(root);
    detachViewerNavigation(root);
    const doc = getDocument(root);
    const win = doc.defaultView || window;
    let lastTurn = -Infinity; // the time of the last page turn; none yet
    let turnToEnd = () => false; // a key or wheel turn back is about to show its new page from the bottom (settle)
    let wheel = { lastAt: 0, lastInsideAt: 0, direction: 0, accumulated: 0 };

    const atTop = (pane) => pane.scrollTop <= 0;
    const atBottom = (pane) => pane.scrollTop + pane.clientHeight >= pane.scrollHeight - 1;

    function turnPage(pane, action, toEnd) {
        lastTurn = win.performance.now();
        wheel.direction = 0;
        wheel.accumulated = 0;
        // The new page's markup arrives with a render batch, before or after the call returns: settle the scroll
        // once the pane shows another page (or give up after a while).
        let settled = false;
        turnToEnd = () => toEnd && !settled;
        const settle = () => {
            if (settled) return;
            settled = true;
            observer.disconnect();
            const current = host.querySelector(".fx-rpt-viewer-panel") || pane;
            current.scrollTop = toEnd ? current.scrollHeight : 0;
        };
        const observer = new win.MutationObserver(() => win.requestAnimationFrame(() => win.requestAnimationFrame(settle)));
        observer.observe(pane, { attributeFilter: ["data-current-page"] });
        const giveUp = win.setTimeout(() => { settled = true; observer.disconnect(); }, 3000);
        dotNetRef.invokeMethodAsync("OnViewerNavigate", action).then((moved) => {
            if (moved) return;
            win.clearTimeout(giveUp);
            settled = true;
            observer.disconnect();
        }).catch(() => { win.clearTimeout(giveUp); settled = true; observer.disconnect(); });
        return true;
    }

    // Called by the document's router for a key that is this viewer's.
    const onKeyDown = (event) => {
        const own = paneOf(event.target);
        const pane = own && host.contains(own) ? own : host.querySelector(".fx-rpt-viewer-panel");
        if (!pane) return;
        let handled = false;
        switch (event.key) {
            case "PageDown":
                if (event.ctrlKey) return;
                if (atBottom(pane)) handled = turnPage(pane, "next", false);
                else { pane.scrollBy({ top: pane.clientHeight * 0.9 }); handled = true; }
                break;
            case "PageUp":
                if (event.ctrlKey) return;
                if (atTop(pane)) handled = turnPage(pane, "previous", true);
                else { pane.scrollBy({ top: -pane.clientHeight * 0.9 }); handled = true; }
                break;
            case "Home":
                if (event.ctrlKey) handled = turnPage(pane, "first", false);
                else { pane.scrollTo({ top: 0 }); handled = true; }
                break;
            case "End":
                if (event.ctrlKey) handled = turnPage(pane, "last", false);
                else { pane.scrollTo({ top: pane.scrollHeight }); handled = true; }
                break;
            default:
                return;
        }
        if (!handled) return;
        event.preventDefault();
        // The viewer keeps the keyboard from here on.
        if (doc.activeElement !== pane && !paneOf(doc.activeElement)) pane.focus({ preventScroll: true });
    };

    const onWheel = (event) => {
        if (event.ctrlKey || event.defaultPrevented) return;
        const pane = paneOf(event.target);
        if (!pane || !host.contains(pane)) return;
        const direction = event.deltaY > 0 ? 1 : event.deltaY < 0 ? -1 : 0;
        if (!direction) return;
        const now = win.performance.now();
        const gap = now - wheel.lastAt;
        wheel.lastAt = now;
        // The notches of the movement that turned the page would scroll the new page away from where the turn put it.
        if (now - lastTurn < 600) { event.preventDefault(); return; }
        if (!(direction > 0 ? atBottom(pane) : atTop(pane))) {
            wheel.lastInsideAt = now;
            wheel.direction = 0;
            wheel.accumulated = 0;
            return;
        }
        // The movement that reached the edge and its tail (within 400 ms of the last in-page scroll) stay on the page.
        if (now - wheel.lastInsideAt < 400) return;
        if (wheel.direction !== direction) { wheel.direction = direction; wheel.accumulated = 0; }
        wheel.accumulated += Math.abs(event.deltaY) * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? pane.clientHeight : 1);
        // A fresh movement (after a pause) turns the page at once; a continuous one after about three notches.
        if (gap < 120 && wheel.accumulated < 300) return;
        if (turnPage(pane, direction > 0 ? "next" : "previous", direction < 0)) event.preventDefault();
    };

    // A page reached by the toolbar, the page box, a find, the group tree or the host is shown from its top (a key or
    // wheel turn back is left to its settle). An anchor scroll applies a pending page change first (showPages).
    const showFromTop = (records) => { for (const record of records) if (!turnToEnd()) record.target.scrollTop = 0; };
    const pages = new win.MutationObserver(showFromTop);
    pages.observe(host, { attributeFilter: ["data-current-page"], subtree: true });
    const binding = { host, doc, onKeyDown, onWheel, pages, showPages: () => showFromTop(pages.takeRecords()) };
    routeKeys(doc, binding);
    host.addEventListener("wheel", onWheel, { passive: false });
    navigationBindings.set(host, binding);

    // A freshly opened report takes the keyboard when nothing else holds it.
    const pane = host.querySelector(".fx-rpt-viewer-panel");
    if (pane && (!doc.activeElement || doc.activeElement === doc.body)) pane.focus({ preventScroll: true });
}

export function detachViewerNavigation(root) {
    const host = getHost(root);
    const binding = navigationBindings.get(host);
    if (!binding) return;
    unrouteKeys(binding.doc, binding);
    host.removeEventListener("wheel", binding.onWheel);
    binding.pages.disconnect();
    navigationBindings.delete(host);
}

// scrollIntoView also scrolls hidden ancestors, moving the toolbar beneath host tabs.
// Only the report's own viewport should move when navigating a group.
function scrollReportAnchor(root, element) {
    const pane = element.closest(".fx-rpt-viewer-panel, .fx-report-container");
    if (!pane || (root && !root.contains(pane))) return;
    navigationBindings.get(getHost(root))?.showPages();
    const target = element.getBoundingClientRect();
    const bounds = pane.getBoundingClientRect();
    const scale = pane.offsetHeight > 0 ? bounds.height / pane.offsetHeight : 1;
    pane.scrollTo({ top: pane.scrollTop + (target.top - bounds.top) / scale - pane.clientTop
        - (pane.clientHeight - target.height / scale) / 2, behavior: "smooth" });
}

export function scrollToAnchor(root, anchorId) {
    waitForElement(root, anchorId, (el) => {
        if (!el) return;

        scrollReportAnchor(root, el);
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
        scrollReportAnchor(root, row);
    });
}

export function clearGroupHighlight(root) {
    const host = getHost(root);
    const previous = host.querySelectorAll
        ? host.querySelectorAll(".fx-rpt-group-active")
        : document.querySelectorAll(".fx-rpt-group-active");

    previous.forEach(el => el.classList.remove("fx-rpt-group-active"));
}

// Only DOM measurement/painting lives here. The component owns the selected mode and tab identity.
// A sized frame accounts for the transformed paper in scroll layout; the viewport/toolbar never scale.
const zoomBindings = new WeakMap();
export function updateViewerZoom(root, dotNetRef, token, level) {
    if (!root) return;
    let binding = zoomBindings.get(root);
    if (!binding) {
        const win = getDocument(root).defaultView || window;
        binding = { win, frame: null, pane: null, paper: null, raf: 0, scale: 1, paintedToken: null, reported: null };
        binding.schedule = () => {
            if (!binding.raf) binding.raf = win.requestAnimationFrame(() => {
                binding.raf = 0;
                paintViewerZoom(root, binding);
            });
        };
        binding.resize = new win.ResizeObserver(binding.schedule);
        binding.mutations = new win.MutationObserver(binding.schedule);
        binding.mutations.observe(root, { childList: true, subtree: true });
        zoomBindings.set(root, binding);
    }
    Object.assign(binding, { dotNetRef, token, level });
    binding.schedule();
}

function paintViewerZoom(root, state) {
    const pane = root.querySelector('.fx-rpt-viewer-panel');
    const frame = pane?.querySelector('.fx-rpt-zoom-frame');
    const paper = frame?.querySelector('.fx-report-page');
    if (!pane || !frame || !paper) return;
    if (state.pane !== pane || state.paper !== paper) {
        state.resize.disconnect();
        state.resize.observe(pane);
        state.resize.observe(paper);
        // Runtime diagnostics above the paper consume real viewport space too.
        for (const sibling of pane.children) if (sibling !== frame.parentElement) state.resize.observe(sibling);
        Object.assign(state, { pane, frame, paper, paintedToken: null, reported: null });
    }
    if (!pane.clientWidth || !pane.clientHeight || !paper.offsetWidth || !paper.offsetHeight) return;
    const css = state.win.getComputedStyle(pane), pagesCss = state.win.getComputedStyle(frame.parentElement);
    const px = (style, name) => parseFloat(style[name]) || 0;
    const horizontal = px(css, 'paddingLeft') + px(css, 'paddingRight') + px(pagesCss, 'paddingLeft') + px(pagesCss, 'paddingRight');
    const vertical = px(css, 'paddingTop') + px(css, 'paddingBottom') + px(pagesCss, 'paddingTop') + px(pagesCss, 'paddingBottom');
    const siblings = [...pane.children].filter(e => e !== frame.parentElement).reduce((sum, e) => sum + e.offsetHeight, 0);
    const width = Math.max(1, pane.clientWidth - horizontal - 1);
    const height = Math.max(1, pane.clientHeight - vertical - siblings - 1);
    const scale = state.level > 0 ? state.level / 100 : state.level === 0
        ? Math.min(width / paper.offsetWidth, height / paper.offsetHeight) : width / paper.offsetWidth;
    const bounds = pane.getBoundingClientRect(), previous = paper.getBoundingClientRect();
    // Keep the same document point under the viewport centre when zoom changes within a page.
    const preserve = state.paintedToken === state.token;
    const cx = bounds.left + pane.clientLeft + pane.clientWidth / 2;
    const cy = bounds.top + pane.clientTop + pane.clientHeight / 2;
    const x = (cx - previous.left) / state.scale, y = (cy - previous.top) / state.scale;
    frame.style.width = `${Math.ceil(paper.offsetWidth * scale)}px`;
    frame.style.height = `${Math.ceil(paper.offsetHeight * scale)}px`;
    frame.dataset.zoomReady = 'true';
    paper.style.transform = `scale(${scale})`;
    if (preserve && Math.abs(scale - state.scale) > .00001) {
        const current = paper.getBoundingClientRect();
        pane.scrollLeft += current.left + x * scale - cx;
        pane.scrollTop += current.top + y * scale - cy;
    }
    state.scale = scale;
    state.paintedToken = state.token;
    const reported = `${state.token}:${state.level}:${scale.toFixed(5)}`;
    if (state.reported !== reported) {
        state.reported = reported;
        state.dotNetRef.invokeMethodAsync('OnViewerZoomMeasured', state.token, state.level, scale).catch(() => {});
    }
}

export function detachViewerZoom(root) {
    const state = zoomBindings.get(root);
    if (!state) return;
    state.resize.disconnect();
    state.mutations.disconnect();
    state.win.cancelAnimationFrame(state.raf);
    if (state.frame) { state.frame.style.width = ''; state.frame.style.height = ''; delete state.frame.dataset.zoomReady; }
    if (state.paper) state.paper.style.transform = '';
    zoomBindings.delete(root);
}
