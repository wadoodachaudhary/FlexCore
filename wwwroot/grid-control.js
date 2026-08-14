/* FlexKit — browser-side helpers for GridControl.
 *
 * Loaded on demand by GridControl via Blazor's dynamic-import interop:
 *   _gridJsModule = await JsRuntime.InvokeAsync<IJSObjectReference>(
 *       "import", GridJsModulePath);
 *
 * One function per concern, exported as ES module bindings so the host
 * page doesn't get a global namespace dumped into it. Keep this file tiny
 * — unscoped grid visuals go in fx-grid-core.css (injected below), since
 * builder-rendered grid elements carry no Blazor scope attribute and the
 * scoped GridControl.razor.css can never match them.
 */

// Self-delivered core CSS: injected from here so every consumer app gets the
// drag/batch-edit visuals with no host wiring. Runs once per module load.
(function ensureGridCoreStyles() {
    const id = "fx-grid-core-styles";
    if (document.getElementById(id)) return;
    const link = document.createElement("link");
    link.id = id;
    link.rel = "stylesheet";
    link.href = new URL("fx-grid-core.css", import.meta.url).href;
    document.head.appendChild(link);
})();

/**
 * Focuses an <input> and selects its entire contents. Used to back the
 * GridColumn.SelectAllOnEdit opt-in: when a batch-edit input is rendered
 * for a column flagged with SelectAllOnEdit, GridControl's
 * OnAfterRenderAsync calls this so the user's first keystroke replaces
 * the old value instead of appending to it.
 *
 * No-ops on non-input elements (e.g. a future date/picker template)
 * rather than throwing — the worst case is that the user's caret lands
 * normally, which is acceptable degradation.
 */
/**
 * Removes any active text-selection ranges from the document. Called
 * from C# at the start of a drag-select gesture so any leftover
 * browser-native selection from a previous interaction (or from the
 * mousedown itself, on browsers that don't honour user-select: none
 * mid-event) doesn't carry over into the new gesture. Cheap and safe
 * to call when nothing is selected.
 */
export function clearTextSelection() {
    try {
        const sel = window.getSelection ? window.getSelection() : null;
        if (sel && typeof sel.removeAllRanges === "function") {
            sel.removeAllRanges();
        }
    } catch (_) {
        /* best-effort */
    }
}

const headerDragPreviewBindings = new WeakMap();
const dragPreviewElementByDocument = new WeakMap();
const headerDropIndicatorByContent = new WeakMap();
const rowDragAutoScrollBindings = new WeakMap();
const gridKeyboardTrapBindings = new WeakMap();
const gridScrollSyncBindings = new WeakMap();
const filterPopupDragBindings = new WeakMap();
const gridResizeCaptureBindings = new WeakMap();
const horizontalBoundaryKeyState = new WeakMap();
const defaultHeaderReorderPipeColor = "#2b2b2b";
const headerAutoScrollEdgePx = 56;
const headerAutoScrollMaxPx = 24;
const rowAutoScrollEdgePx = 48;
const rowAutoScrollMaxPx = 18;
const rowDragStartThresholdPx = 3;
let caretMeasureCanvas;

function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
}

function ensureHeaderDragPreviewElement(doc) {
    let el = dragPreviewElementByDocument.get(doc);
    if (el && el.isConnected) return el;

    el = doc.createElement("div");
    el.setAttribute("aria-hidden", "true");
    Object.assign(el.style, {
        position: "fixed",
        top: "0",
        left: "0",
        width: "34px",
        height: "12px",
        border: "1px solid #1a73e8",
        borderRadius: "2px",
        background: "#e7f1ff",
        boxShadow: "inset 0 0 0 1px rgba(26,115,232,0.28)",
        pointerEvents: "none",
        opacity: "0.01",
        zIndex: "-1"
    });

    doc.body.appendChild(el);
    dragPreviewElementByDocument.set(doc, el);
    return el;
}

function getGridContentElement(gridRoot) {
    return gridRoot.querySelector(".fx-grid-content");
}

function getGridBodyViewportElement(gridRoot) {
    return gridRoot.querySelector(".fx-grid-body-viewport") || getGridContentElement(gridRoot);
}

function getGridHorizontalViewportElement(gridRoot) {
    if (gridRoot.classList?.contains("fx-grid-vscroll-header-gutter")) {
        return gridRoot.querySelector(".fx-grid-scroll-surface") || getGridBodyViewportElement(gridRoot);
    }
    return gridRoot.querySelector(".fx-grid-body-viewport") || getGridContentElement(gridRoot);
}

function getGridVerticalViewportElement(gridRoot) {
    if (gridRoot.classList?.contains("fx-grid-vscroll-header-gutter")) {
        return gridRoot.querySelector(".fx-grid-scroll-surface") || getGridBodyViewportElement(gridRoot);
    }
    return getGridBodyViewportElement(gridRoot);
}

function getGridScrollResetTargets(gridRoot) {
    return [
        getGridContentElement(gridRoot),
        gridRoot.querySelector(".fx-grid-scroll-surface"),
        gridRoot.querySelector(".fx-grid-header-viewport"),
        gridRoot.querySelector(".fx-grid-body-frame"),
        gridRoot.querySelector(".fx-grid-body-viewport")
    ].filter(Boolean);
}

function resetGridScrollTargets(gridRoot) {
    const targets = new Set(getGridScrollResetTargets(gridRoot));
    for (const target of targets) {
        target.scrollTop = 0;
        target.scrollLeft = 0;
    }
}

export function resetInitialGridScroll(gridRoot) {
    if (!gridRoot) return;

    const doc = gridRoot.ownerDocument || document;
    const win = doc.defaultView || window;
    const requestFrame = typeof win.requestAnimationFrame === "function"
        ? win.requestAnimationFrame.bind(win)
        : callback => win.setTimeout(callback, 0);

    resetGridScrollTargets(gridRoot);
    requestFrame(() => {
        resetGridScrollTargets(gridRoot);
        requestFrame(() => resetGridScrollTargets(gridRoot));
    });
    win.setTimeout(() => resetGridScrollTargets(gridRoot), 0);
    win.setTimeout(() => resetGridScrollTargets(gridRoot), 80);
}

export function registerFilterPopupDrag(gridRoot) {
    if (!gridRoot) return;

    const popup = gridRoot.querySelector(".fx-filter-popup");
    const header = popup?.querySelector(".fx-filter-popup-header");
    if (!popup || !header || filterPopupDragBindings.has(popup)) return;

    const doc = popup.ownerDocument || document;
    const win = doc.defaultView || window;
    const state = {
        dragging: false,
        pointerOffsetX: 0,
        pointerOffsetY: 0
    };

    const pinPopupToViewport = (left, top) => {
        const width = popup.offsetWidth || 340;
        const height = popup.offsetHeight || 240;
        const maxLeft = Math.max(4, win.innerWidth - width - 4);
        const maxTop = Math.max(4, win.innerHeight - height - 4);
        popup.style.position = "fixed";
        popup.style.left = `${Math.round(clamp(left, 4, maxLeft))}px`;
        popup.style.top = `${Math.round(clamp(top, 4, maxTop))}px`;
        popup.style.right = "auto";
        popup.style.bottom = "auto";
    };

    const onMouseDown = (event) => {
        if (event.button !== 0) return;
        const target = event.target instanceof Element ? event.target : null;
        if (target?.closest?.("button, input, select, textarea, a, [contenteditable='true'], [contenteditable='']")) {
            return;
        }

        const rect = popup.getBoundingClientRect();
        state.dragging = true;
        state.pointerOffsetX = event.clientX - rect.left;
        state.pointerOffsetY = event.clientY - rect.top;
        pinPopupToViewport(rect.left, rect.top);
        event.preventDefault();
    };

    const onMouseMove = (event) => {
        if (!state.dragging) return;
        pinPopupToViewport(event.clientX - state.pointerOffsetX, event.clientY - state.pointerOffsetY);
    };

    const onMouseUp = () => {
        state.dragging = false;
    };

    header.addEventListener("mousedown", onMouseDown);
    doc.addEventListener("mousemove", onMouseMove, true);
    doc.addEventListener("mouseup", onMouseUp, true);

    filterPopupDragBindings.set(popup, { header, onMouseDown, onMouseMove, onMouseUp });

    const requestFrame = typeof win.requestAnimationFrame === "function"
        ? win.requestAnimationFrame.bind(win)
        : callback => win.setTimeout(callback, 0);

    requestFrame(() => {
        const rect = popup.getBoundingClientRect();
        pinPopupToViewport(rect.left, rect.top);
    });
}

export function registerGridResizeCapture(gridRoot, dotNetRef, initialClientX = 0, initialClientY = 0) {
    if (!gridRoot || !dotNetRef) return;

    unregisterGridResizeCapture(gridRoot);

    const doc = gridRoot.ownerDocument || document;
    let ended = false;
    let lastClientX = Number(initialClientX) || 0;
    let lastClientY = Number(initialClientY) || 0;

    const content = gridRoot.querySelector(".fx-grid-content");

    // Dragging a column past the viewport edge scrolls the surface in the same
    // gesture, so the grip and the widening column never leave view.
    const followPointer = (event) => {
        if (!content) return;
        const rect = content.getBoundingClientRect();
        const margin = 24;
        if (event.clientX > rect.right - margin)
            content.scrollLeft += event.clientX - (rect.right - margin);
        else if (event.clientX < rect.left + margin)
            content.scrollLeft -= (rect.left + margin) - event.clientX;
    };

    const invokeMove = (event) => {
        if (ended) return;
        lastClientX = event.clientX;
        lastClientY = event.clientY;
        followPointer(event);
        dotNetRef.invokeMethodAsync("ContinueGridResizeFromBrowserAsync", event.clientX, event.clientY)
            .catch(() => {});
    };

    const invokeEnd = (event) => {
        if (ended) return;
        ended = true;
        cleanup();
        dotNetRef.invokeMethodAsync("EndGridResizeFromBrowserAsync", event.clientX, event.clientY)
            .catch(() => {});
    };

    const onMouseMove = (event) => {
        event.preventDefault();
        invokeMove(event);
    };

    const onMouseUp = (event) => {
        event.preventDefault();
        invokeEnd(event);
    };

    const onWindowBlur = () => {
        if (ended) return;
        ended = true;
        cleanup();
        dotNetRef.invokeMethodAsync("EndGridResizeFromBrowserAsync", lastClientX, lastClientY)
            .catch(() => {});
    };

    const cleanup = () => {
        doc.removeEventListener("mousemove", onMouseMove, true);
        doc.removeEventListener("mouseup", onMouseUp, true);
        (doc.defaultView || window).removeEventListener("blur", onWindowBlur, true);
        gridResizeCaptureBindings.delete(gridRoot);
    };

    doc.addEventListener("mousemove", onMouseMove, true);
    doc.addEventListener("mouseup", onMouseUp, true);
    (doc.defaultView || window).addEventListener("blur", onWindowBlur, true);
    gridResizeCaptureBindings.set(gridRoot, { cleanup });
}

export function unregisterGridResizeCapture(gridRoot) {
    const state = gridResizeCaptureBindings.get(gridRoot);
    if (!state) return;
    state.cleanup();
}

const scrollbarActivityByRoot = new WeakMap();

export function registerScrollbarActivity(gridRoot) {
    if (!gridRoot) return;

    unregisterScrollbarActivity(gridRoot);

    const targets = [
        gridRoot.querySelector(".fx-grid-scroll-surface"),
        gridRoot.querySelector(".fx-grid-body-viewport")
    ].filter(Boolean);

    let hideTimer = 0;
    const markScrolling = () => {
        gridRoot.classList.add("fx-grid-scrolling");
        if (hideTimer) {
            window.clearTimeout(hideTimer);
        }
        hideTimer = window.setTimeout(() => {
            gridRoot.classList.remove("fx-grid-scrolling");
            hideTimer = 0;
        }, 650);
    };

    for (const target of targets) {
        target.addEventListener("scroll", markScrolling, { passive: true });
    }
    gridRoot.addEventListener("wheel", markScrolling, { passive: true });
    gridRoot.addEventListener("keydown", markScrolling);

    scrollbarActivityByRoot.set(gridRoot, { targets, markScrolling, getTimer: () => hideTimer });
}

export function unregisterScrollbarActivity(gridRoot) {
    const state = scrollbarActivityByRoot.get(gridRoot);
    if (!state) return;

    for (const target of state.targets) {
        target.removeEventListener("scroll", state.markScrolling);
    }
    gridRoot.removeEventListener("wheel", state.markScrolling);
    gridRoot.removeEventListener("keydown", state.markScrolling);

    const timer = state.getTimer();
    if (timer) {
        window.clearTimeout(timer);
    }

    gridRoot.classList.remove("fx-grid-scrolling");
    scrollbarActivityByRoot.delete(gridRoot);
}

export function registerGridScrollSync(gridRoot) {
    if (!gridRoot) return;

    unregisterGridScrollSync(gridRoot);

    const headerViewport = gridRoot.querySelector(".fx-grid-header-viewport");
    const horizontalViewport = getGridHorizontalViewportElement(gridRoot);
    if (!headerViewport || !horizontalViewport) return;

    let syncing = false;
    const syncHeaderFromBody = () => {
        if (syncing) return;
        syncing = true;
        headerViewport.scrollLeft = horizontalViewport.scrollLeft;
        syncing = false;
    };
    const syncBodyFromHeader = () => {
        if (syncing) return;
        syncing = true;
        horizontalViewport.scrollLeft = headerViewport.scrollLeft;
        syncing = false;
    };

    horizontalViewport.addEventListener("scroll", syncHeaderFromBody, { passive: true });
    headerViewport.addEventListener("scroll", syncBodyFromHeader, { passive: true });
    syncHeaderFromBody();

    gridScrollSyncBindings.set(gridRoot, {
        headerViewport,
        horizontalViewport,
        syncHeaderFromBody,
        syncBodyFromHeader
    });
}

export function unregisterGridScrollSync(gridRoot) {
    const state = gridScrollSyncBindings.get(gridRoot);
    if (!state) return;

    state.horizontalViewport.removeEventListener("scroll", state.syncHeaderFromBody);
    state.headerViewport.removeEventListener("scroll", state.syncBodyFromHeader);
    gridScrollSyncBindings.delete(gridRoot);
}

function getHeaderReorderPipeColor(gridRoot) {
    try {
        const value = window.getComputedStyle(gridRoot)
            .getPropertyValue("--fx-grid-reorder-pipe-color")
            .trim();
        return value || defaultHeaderReorderPipeColor;
    } catch (_) {
        return defaultHeaderReorderPipeColor;
    }
}

function applyHeaderDropIndicatorColor(gridRoot, indicator) {
    indicator.style.background = getHeaderReorderPipeColor(gridRoot);
    indicator.style.boxShadow = "0 0 0 1px rgba(0,0,0,0.28)";
}

function ensureHeaderDropIndicator(gridRoot, contentEl) {
    let indicator = headerDropIndicatorByContent.get(contentEl);
    if (indicator && indicator.isConnected) {
        applyHeaderDropIndicatorColor(gridRoot, indicator);
        return indicator;
    }

    indicator = contentEl.ownerDocument.createElement("div");
    indicator.setAttribute("aria-hidden", "true");
    indicator.className = "fx-drag-drop-indicator";
    Object.assign(indicator.style, {
        position: "absolute",
        top: "0",
        bottom: "0",
        width: "3px",
        pointerEvents: "none",
        display: "none",
        zIndex: "6"
    });
    applyHeaderDropIndicatorColor(gridRoot, indicator);

    // Make indicator positioning relative to grid content viewport.
    if (window.getComputedStyle(contentEl).position === "static") {
        contentEl.style.position = "relative";
    }

    contentEl.appendChild(indicator);
    headerDropIndicatorByContent.set(contentEl, indicator);
    return indicator;
}

function hideHeaderDropIndicator(gridRoot) {
    const contentEl = getGridContentElement(gridRoot);
    if (!contentEl) return;
    const indicator = headerDropIndicatorByContent.get(contentEl);
    if (!indicator) return;
    indicator.style.display = "none";
}

function updateHeaderDropIndicator(gridRoot, headerEl, eventOrClientX) {
    const contentEl = getGridContentElement(gridRoot);
    if (!contentEl) return;

    const indicator = ensureHeaderDropIndicator(gridRoot, contentEl);
    applyHeaderDropIndicatorColor(gridRoot, indicator);
    const contentRect = contentEl.getBoundingClientRect();
    const headerRect = headerEl.getBoundingClientRect();

    const clientX = typeof eventOrClientX === "number"
        ? eventOrClientX
        : typeof eventOrClientX?.clientX === "number"
            ? eventOrClientX.clientX
        : headerRect.left + (headerRect.width / 2);

    const insertRight = (clientX - headerRect.left) >= (headerRect.width / 2);
    const boundaryX = insertRight ? headerRect.right : headerRect.left;
    const x = boundaryX - contentRect.left + contentEl.scrollLeft;

    indicator.style.left = `${Math.round(x - 1)}px`;
    indicator.style.display = "block";
}

function getHeaderAutoScrollDelta(contentEl, clientX) {
    if (!Number.isFinite(clientX)) return 0;

    const maxScrollLeft = contentEl.scrollWidth - contentEl.clientWidth;
    if (maxScrollLeft <= 0) return 0;

    const rect = contentEl.getBoundingClientRect();
    const leftIntensity = clamp((headerAutoScrollEdgePx - (clientX - rect.left)) / headerAutoScrollEdgePx, 0, 1);
    const rightIntensity = clamp((headerAutoScrollEdgePx - (rect.right - clientX)) / headerAutoScrollEdgePx, 0, 1);

    if (leftIntensity > 0 && contentEl.scrollLeft > 0) {
        return -Math.max(1, Math.round(headerAutoScrollMaxPx * leftIntensity));
    }

    if (rightIntensity > 0 && contentEl.scrollLeft < maxScrollLeft) {
        return Math.max(1, Math.round(headerAutoScrollMaxPx * rightIntensity));
    }

    return 0;
}

function recordHeaderDragPosition(state, event) {
    if (typeof event?.clientX === "number") {
        state.lastClientX = event.clientX;
    }
}

function stopHeaderAutoScroll(state) {
    if (!state.autoScrollFrame) return;

    const win = state.autoScrollWindow || window;
    win.cancelAnimationFrame(state.autoScrollFrame);
    state.autoScrollFrame = 0;
}

function scheduleHeaderAutoScroll(gridRoot, state) {
    if (state.autoScrollFrame) return;

    const doc = gridRoot.ownerDocument || document;
    const win = doc.defaultView || window;
    state.autoScrollWindow = win;

    const tick = () => {
        state.autoScrollFrame = 0;
        if (!state.activeHeaderDrag) return;

        const contentEl = getGridContentElement(gridRoot);
        if (!contentEl) return;

        const delta = getHeaderAutoScrollDelta(contentEl, state.lastClientX);
        if (delta !== 0) {
            const maxScrollLeft = contentEl.scrollWidth - contentEl.clientWidth;
            const before = contentEl.scrollLeft;
            contentEl.scrollLeft = clamp(before + delta, 0, maxScrollLeft);

            if (contentEl.scrollLeft !== before && state.lastHeader?.isConnected) {
                updateHeaderDropIndicator(gridRoot, state.lastHeader, state.lastClientX);
            }
        }

        scheduleHeaderAutoScroll(gridRoot, state);
    };

    state.autoScrollFrame = win.requestAnimationFrame(tick);
}

function getRowAutoScrollDelta(contentEl, clientY) {
    if (!Number.isFinite(clientY)) return 0;

    const maxScrollTop = contentEl.scrollHeight - contentEl.clientHeight;
    if (maxScrollTop <= 0) return 0;

    const rect = contentEl.getBoundingClientRect();
    const topIntensity = clamp((rowAutoScrollEdgePx - (clientY - rect.top)) / rowAutoScrollEdgePx, 0, 1);
    const bottomIntensity = clamp((rowAutoScrollEdgePx - (rect.bottom - clientY)) / rowAutoScrollEdgePx, 0, 1);

    if (topIntensity > 0 && contentEl.scrollTop > 0) {
        return -Math.max(1, Math.round(rowAutoScrollMaxPx * topIntensity));
    }

    if (bottomIntensity > 0 && contentEl.scrollTop < maxScrollTop) {
        return Math.max(1, Math.round(rowAutoScrollMaxPx * bottomIntensity));
    }

    return 0;
}

function getDataRowAtPointer(contentEl, clientX, clientY) {
    if (!Number.isFinite(clientX) || !Number.isFinite(clientY)) return null;

    const rect = contentEl.getBoundingClientRect();
    const x = clamp(clientX, rect.left + 1, rect.right - 1);
    const y = clamp(clientY, rect.top + 1, rect.bottom - 1);
    const doc = contentEl.ownerDocument || document;
    const target = doc.elementFromPoint(x, y);
    const row = target?.closest?.(".fx-grid-body .fx-row, .fx-row");
    return row && contentEl.contains(row) ? row : null;
}

function notifyRowDragSelection(row, state, contentEl) {
    if (!row || (state.lastRow === row && row.isConnected)) return;

    state.lastRow = row;
    const rows = Array.from(contentEl.querySelectorAll(".fx-grid-body .fx-row"));
    const rowIndex = rows.indexOf(row);
    if (rowIndex < 0 || rowIndex === state.lastRowIndex) return;

    state.lastRowIndex = rowIndex;
    if (state.dotNetRef && typeof state.dotNetRef.invokeMethodAsync === "function") {
        state.dotNetRef.invokeMethodAsync("ContinueRowDragSelectionFromBrowserAsync", rowIndex)
            .catch(() => { /* best-effort */ });
        return;
    }

    const doc = row.ownerDocument || document;
    const win = doc.defaultView || window;
    row.dispatchEvent(new MouseEvent("mouseenter", {
        bubbles: true,
        cancelable: false,
        view: win,
        clientX: state.lastClientX,
        clientY: state.lastClientY,
        button: 0,
        buttons: 1
    }));
}

function stopRowAutoScroll(state) {
    if (!state.autoScrollFrame) return;

    const win = state.autoScrollWindow || window;
    win.cancelAnimationFrame(state.autoScrollFrame);
    state.autoScrollFrame = 0;
}

function scheduleRowAutoScroll(gridRoot, state) {
    if (state.autoScrollFrame || !state.hasMoved) return;

    const doc = gridRoot.ownerDocument || document;
    const win = doc.defaultView || window;
    state.autoScrollWindow = win;

    const tick = () => {
        state.autoScrollFrame = 0;
        if (!state.activeRowDrag || !state.hasMoved) return;

        const contentEl = getGridVerticalViewportElement(gridRoot);
        if (!contentEl) return;

        const delta = getRowAutoScrollDelta(contentEl, state.lastClientY);
        if (delta !== 0) {
            const maxScrollTop = contentEl.scrollHeight - contentEl.clientHeight;
            const before = contentEl.scrollTop;
            contentEl.scrollTop = clamp(before + delta, 0, maxScrollTop);

            if (contentEl.scrollTop !== before) {
                notifyRowDragSelection(
                    getDataRowAtPointer(contentEl, state.lastClientX, state.lastClientY),
                    state,
                    contentEl);
            }
        }

        scheduleRowAutoScroll(gridRoot, state);
    };

    state.autoScrollFrame = win.requestAnimationFrame(tick);
}

function isInteractiveDragSource(target) {
    return !!target?.closest?.("input, button, select, textarea, a, [contenteditable='true'], [contenteditable='']");
}

function shouldTrapGridKeyboardNavigation(gridRoot, target) {
    const active = gridRoot.ownerDocument?.activeElement ?? null;
    if (active === gridRoot) return true;
    if (active instanceof Element && gridRoot.contains(active)) return true;
    if (!target || !gridRoot.contains(target)) return false;
    if (target === gridRoot) return true;
    return !!target.closest?.(".fx-cell, .fx-batch-input, .fx-cell-edit-btn, .fx-grid-popup-btn");
}

function isTextCaretNavigationTarget(target) {
    if (!(target instanceof Element)) return false;
    if (target.matches?.("textarea, [contenteditable='true'], [contenteditable='']")) return true;
    if (!target.matches?.("input")) return false;

    const type = (target.getAttribute("type") || "text").toLowerCase();
    return !["button", "checkbox", "radio", "submit", "reset", "file", "image", "range", "color"].includes(type);
}

function readTextSelection(target) {
    if (!isTextCaretNavigationTarget(target)) return null;

    if (target.matches?.("[contenteditable='true'], [contenteditable='']")) {
        const doc = target.ownerDocument || document;
        const sel = doc.getSelection ? doc.getSelection() : null;
        if (!sel || sel.rangeCount === 0 || !target.contains(sel.anchorNode) || !target.contains(sel.focusNode)) {
            return null;
        }

        // Contenteditable cells are not used by GridControl batch editing today.
        // Treat them as native text editors unless/until a real editor needs edge
        // navigation support.
        return { start: 1, end: 0, length: 0 };
    }

    if (typeof target.selectionStart !== "number" || typeof target.selectionEnd !== "number") {
        return null;
    }

    const value = "value" in target ? (target.value || "") : "";
    return {
        start: target.selectionStart,
        end: target.selectionEnd,
        length: value.length
    };
}

function captureHorizontalBoundaryKeyState(target, key) {
    const selection = readTextSelection(target);
    if (!selection || selection.start !== selection.end) {
        horizontalBoundaryKeyState.delete(target);
        return null;
    }

    const state = {
        key,
        start: selection.start,
        end: selection.end,
        length: selection.length,
        time: Date.now()
    };
    horizontalBoundaryKeyState.set(target, state);
    return state;
}

export function isInputCaretAtHorizontalBoundary(target, key) {
    const selection = readTextSelection(target);
    if (!selection) {
        horizontalBoundaryKeyState.delete(target);
        return false;
    }

    // A selected range should first collapse normally with ArrowLeft/Right; it
    // should not leave the cell until the caret is a single point at the edge.
    if (selection.start !== selection.end) {
        horizontalBoundaryKeyState.delete(target);
        return false;
    }

    const atLeftBoundary = key === "ArrowLeft" && selection.start <= 0;
    const atRightBoundary = key === "ArrowRight" && selection.end >= selection.length;

    if (!atLeftBoundary && !atRightBoundary) {
        horizontalBoundaryKeyState.delete(target);
        return false;
    }

    const keyState = horizontalBoundaryKeyState.get(target);
    horizontalBoundaryKeyState.delete(target);

    if (keyState
        && keyState.key === key
        && keyState.length === selection.length
        && Date.now() - keyState.time < 1500) {
        if (key === "ArrowLeft") return keyState.start <= 0;
        if (key === "ArrowRight") return keyState.end >= keyState.length;
    }

    return key === "ArrowRight" ? atRightBoundary : false;
}

/**
 * Browser navigation keys must be suppressed while focus is inside a data cell/editor;
 * otherwise native focus traversal races Blazor Server's grid navigation and
 * jumps to the next real DOM button/checkbox or moves the input caret instead
 * of the grid cursor. We only trap navigation keys, not typing.
 */
export function registerGridKeyboardTrap(gridRoot) {
    if (!gridRoot || gridKeyboardTrapBindings.has(gridRoot)) return;
    const doc = gridRoot.ownerDocument || document;

    const onKeyDown = (event) => {
        const isGridScrollKey =
            event.key === "PageUp" ||
            event.key === "PageDown" ||
            ((event.ctrlKey || event.metaKey) && (event.key === "Home" || event.key === "End"));
        const isNavigationKey =
            event.key === "Tab" ||
            event.key === "ArrowLeft" ||
            event.key === "ArrowRight" ||
            event.key === "ArrowUp" ||
            event.key === "ArrowDown" ||
            isGridScrollKey;
        if (!isNavigationKey) return;

        if (isGridScrollKey) {
            if (event.altKey || event.shiftKey) return;
            const target = event.target instanceof Element ? event.target : null;
            if (shouldTrapGridKeyboardNavigation(gridRoot, target)) {
                event.preventDefault();
            }
            return;
        }

        if ((event.key !== "Tab" && event.shiftKey) || event.altKey || event.ctrlKey || event.metaKey) return;

        const target = event.target instanceof Element ? event.target : null;
        if ((event.key === "ArrowLeft" || event.key === "ArrowRight")
            && isTextCaretNavigationTarget(target)) {
            const keyState = captureHorizontalBoundaryKeyState(target, event.key);
            const alreadyAtBoundary = event.key === "ArrowLeft"
                ? keyState?.start <= 0
                : keyState?.end >= keyState?.length;
            if (alreadyAtBoundary) {
                event.preventDefault();
            }
            return;
        }

        if (shouldTrapGridKeyboardNavigation(gridRoot, target)) {
            event.preventDefault();
        }
    };

    gridRoot.addEventListener("keydown", onKeyDown, true);
    doc.addEventListener("keydown", onKeyDown, true);
    gridKeyboardTrapBindings.set(gridRoot, { onKeyDown, doc });
}

export function unregisterGridKeyboardTrap(gridRoot) {
    if (!gridRoot) return;
    const handlers = gridKeyboardTrapBindings.get(gridRoot);
    if (!handlers) return;

    gridRoot.removeEventListener("keydown", handlers.onKeyDown, true);
    handlers.doc?.removeEventListener?.("keydown", handlers.onKeyDown, true);
    gridKeyboardTrapBindings.delete(gridRoot);
}

/**
 * Installs a tiny custom drag image for grid header reordering so the
 * default full-width browser ghost doesn't cover drop indicators.
 */
export function registerHeaderDragPreview(gridRoot) {
    if (!gridRoot || headerDragPreviewBindings.has(gridRoot)) return;

    const state = {
        activeHeaderDrag: false,
        autoScrollFrame: 0,
        autoScrollWindow: null,
        lastClientX: Number.NaN,
        lastHeader: null
    };

    const onDragStart = (event) => {
        const target = event.target instanceof Element ? event.target : null;
        if (!target) return;

        const header = target.closest(".fx-header-cell");
        if (!header) return;
        if (!gridRoot.contains(header)) return;
        if (header.getAttribute("draggable") !== "true") return;

        const doc = gridRoot.ownerDocument || document;
        const preview = ensureHeaderDragPreviewElement(doc);
        if (event.dataTransfer) {
            try {
                event.dataTransfer.setDragImage(preview, 8, 6);
            } catch (_) {
                /* best-effort */
            }
        }

        state.activeHeaderDrag = true;
        state.lastHeader = header;
        recordHeaderDragPosition(state, event);
        gridRoot.classList.add("fx-header-drag-active");
        updateHeaderDropIndicator(gridRoot, header, event);
        scheduleHeaderAutoScroll(gridRoot, state);
    };

    const onDragOver = (event) => {
        if (!state.activeHeaderDrag) return;
        recordHeaderDragPosition(state, event);
        scheduleHeaderAutoScroll(gridRoot, state);

        const target = event.target instanceof Element ? event.target : null;
        if (!target) {
            hideHeaderDropIndicator(gridRoot);
            return;
        }

        const header = target.closest(".fx-header-cell");
        if (!header || !gridRoot.contains(header) || header.getAttribute("draggable") !== "true") {
            hideHeaderDropIndicator(gridRoot);
            return;
        }

        state.lastHeader = header;
        updateHeaderDropIndicator(gridRoot, header, event);
    };

    const onDocumentDragOver = (event) => {
        if (!state.activeHeaderDrag) return;
        recordHeaderDragPosition(state, event);
        scheduleHeaderAutoScroll(gridRoot, state);
    };

    const clearDragVisuals = () => {
        state.activeHeaderDrag = false;
        state.lastHeader = null;
        state.lastClientX = Number.NaN;
        stopHeaderAutoScroll(state);
        gridRoot.classList.remove("fx-header-drag-active");
        hideHeaderDropIndicator(gridRoot);
    };

    const onDrop = () => clearDragVisuals();
    const onDragEnd = () => clearDragVisuals();
    const onDragLeave = (event) => {
        if (!state.activeHeaderDrag) return;
        const target = event.target instanceof Element ? event.target : null;
        if (!target || target === gridRoot) {
            hideHeaderDropIndicator(gridRoot);
        }
    };

    gridRoot.addEventListener("dragstart", onDragStart, true);
    gridRoot.addEventListener("dragover", onDragOver, true);
    gridRoot.addEventListener("dragleave", onDragLeave, true);
    gridRoot.addEventListener("drop", onDrop, true);
    gridRoot.addEventListener("dragend", onDragEnd, true);
    (gridRoot.ownerDocument || document).addEventListener("dragover", onDocumentDragOver, true);

    headerDragPreviewBindings.set(gridRoot, {
        onDragStart,
        onDragOver,
        onDocumentDragOver,
        onDragLeave,
        onDrop,
        onDragEnd,
        state
    });
}

export function unregisterHeaderDragPreview(gridRoot) {
    if (!gridRoot) return;
    const handlers = headerDragPreviewBindings.get(gridRoot);
    if (!handlers) return;

    gridRoot.removeEventListener("dragstart", handlers.onDragStart, true);
    gridRoot.removeEventListener("dragover", handlers.onDragOver, true);
    gridRoot.removeEventListener("dragleave", handlers.onDragLeave, true);
    gridRoot.removeEventListener("drop", handlers.onDrop, true);
    gridRoot.removeEventListener("dragend", handlers.onDragEnd, true);
    (gridRoot.ownerDocument || document).removeEventListener("dragover", handlers.onDocumentDragOver, true);

    headerDragPreviewBindings.delete(gridRoot);
    stopHeaderAutoScroll(handlers.state);
    gridRoot.classList.remove("fx-header-drag-active");
    hideHeaderDropIndicator(gridRoot);
}

export function registerRowDragSelectionAutoScroll(gridRoot, dotNetRef) {
    if (!gridRoot || rowDragAutoScrollBindings.has(gridRoot)) return;

    const state = {
        activeRowDrag: false,
        hasMoved: false,
        startClientX: Number.NaN,
        startClientY: Number.NaN,
        lastClientX: Number.NaN,
        lastClientY: Number.NaN,
        lastRow: null,
        lastRowIndex: -1,
        autoScrollFrame: 0,
        autoScrollWindow: null,
        dotNetRef
    };

    const clear = () => {
        state.activeRowDrag = false;
        state.hasMoved = false;
        state.startClientX = Number.NaN;
        state.startClientY = Number.NaN;
        state.lastClientX = Number.NaN;
        state.lastClientY = Number.NaN;
        state.lastRow = null;
        state.lastRowIndex = -1;
        stopRowAutoScroll(state);
    };

    const onMouseDown = (event) => {
        if (event.button !== 0) return;

        const target = event.target instanceof Element ? event.target : null;
        if (!target || isInteractiveDragSource(target)) return;

        const row = target.closest(".fx-grid-body .fx-row, .fx-row");
        if (!row || !gridRoot.contains(row)) return;

        state.activeRowDrag = true;
        state.hasMoved = false;
        state.startClientX = event.clientX;
        state.startClientY = event.clientY;
        state.lastClientX = event.clientX;
        state.lastClientY = event.clientY;
        state.lastRow = row;
    };

    const onMouseMove = (event) => {
        if (!state.activeRowDrag) return;

        if ((event.buttons & 1) === 0) {
            clear();
            return;
        }

        state.lastClientX = event.clientX;
        state.lastClientY = event.clientY;

        if (!state.hasMoved) {
            const movedX = Math.abs(event.clientX - state.startClientX);
            const movedY = Math.abs(event.clientY - state.startClientY);
            state.hasMoved = movedX > rowDragStartThresholdPx || movedY > rowDragStartThresholdPx;
        }

        scheduleRowAutoScroll(gridRoot, state);
    };

    const onMouseUp = () => clear();

    const doc = gridRoot.ownerDocument || document;
    gridRoot.addEventListener("mousedown", onMouseDown, true);
    doc.addEventListener("mousemove", onMouseMove, true);
    doc.addEventListener("mouseup", onMouseUp, true);

    rowDragAutoScrollBindings.set(gridRoot, {
        onMouseDown,
        onMouseMove,
        onMouseUp,
        state
    });
}

export function unregisterRowDragSelectionAutoScroll(gridRoot) {
    if (!gridRoot) return;
    const handlers = rowDragAutoScrollBindings.get(gridRoot);
    if (!handlers) return;

    const doc = gridRoot.ownerDocument || document;
    gridRoot.removeEventListener("mousedown", handlers.onMouseDown, true);
    doc.removeEventListener("mousemove", handlers.onMouseMove, true);
    doc.removeEventListener("mouseup", handlers.onMouseUp, true);
    stopRowAutoScroll(handlers.state);
    rowDragAutoScrollBindings.delete(gridRoot);
}

export function ensureActiveGridCellVisible(gridRoot) {
    if (!gridRoot) return;

    const contentEl = getGridHorizontalViewportElement(gridRoot);
    const bodyViewportEl = getGridVerticalViewportElement(gridRoot);
    const activeCell = gridRoot.querySelector(".fx-cell-active");
    if (!contentEl || !bodyViewportEl || !activeCell) return;

    const outerRect = contentEl.getBoundingClientRect();
    const contentRect = {
        left: outerRect.left,
        top: outerRect.top,
        right: outerRect.left + contentEl.clientWidth,
        bottom: outerRect.top + contentEl.clientHeight
    };
    const outerBodyRect = bodyViewportEl.getBoundingClientRect();
    const bodyRect = {
        left: outerBodyRect.left,
        top: outerBodyRect.top,
        right: outerBodyRect.left + bodyViewportEl.clientWidth,
        bottom: outerBodyRect.top + bodyViewportEl.clientHeight
    };
    const cellRect = activeCell.getBoundingClientRect();
    const padding = 4;

    if (cellRect.left < contentRect.left + padding) {
        contentEl.scrollLeft = Math.max(0, contentEl.scrollLeft - ((contentRect.left + padding) - cellRect.left));
    } else if (cellRect.right > contentRect.right - padding) {
        const maxScrollLeft = contentEl.scrollWidth - contentEl.clientWidth;
        contentEl.scrollLeft = clamp(
            contentEl.scrollLeft + (cellRect.right - (contentRect.right - padding)),
            0,
            maxScrollLeft);
    }

    if (cellRect.top < bodyRect.top + padding) {
        bodyViewportEl.scrollTop = Math.max(0, bodyViewportEl.scrollTop - ((bodyRect.top + padding) - cellRect.top));
    } else if (cellRect.bottom > bodyRect.bottom - padding) {
        const maxScrollTop = bodyViewportEl.scrollHeight - bodyViewportEl.clientHeight;
        bodyViewportEl.scrollTop = clamp(
            bodyViewportEl.scrollTop + (cellRect.bottom - (bodyRect.bottom - padding)),
            0,
            maxScrollTop);
    }
}

export function focusInputAtEnd(el) {
    if (!el) return;
    try {
        if (typeof el.focus === "function") {
            el.focus({ preventScroll: true });
        }
        const value = "value" in el ? (el.value || "") : "";
        if (typeof el.setSelectionRange === "function") {
            const end = value.length;
            el.setSelectionRange(end, end);
        }
    } catch (_) {
        /* best-effort */
    }
}

export function selectAllInputContents(el) {
    if (!el) return;
    try {
        // Focus first so the subsequent .select() actually shows the
        // selection highlight. Without focus, .select() succeeds at the
        // selection API level but the user sees nothing.
        if (typeof el.focus === "function") {
            el.focus({ preventScroll: true });
        }
        if (typeof el.select === "function") {
            el.select();
            return;
        }
        // Fallback for elements that don't expose .select() (textareas
        // exposed as contenteditable, custom controls, …): use the
        // selection API to span the element's text content.
        if (typeof el.setSelectionRange === "function" && "value" in el) {
            el.setSelectionRange(0, (el.value || "").length);
        }
    } catch (_) {
        /* best-effort — caret will just land normally */
    }
}

export function focusInputAtClientX(el, clientX) {
    if (!el) return;
    try {
        if (typeof el.focus === "function") {
            el.focus({ preventScroll: true });
        }

        if (typeof el.setSelectionRange !== "function" || !("value" in el)) {
            return;
        }

        const value = el.value || "";
        if (!value.length || typeof clientX !== "number" || Number.isNaN(clientX)) {
            const end = value.length;
            el.setSelectionRange(end, end);
            return;
        }

        const index = estimateInputCaretIndex(el, clientX, value);
        el.setSelectionRange(index, index);
    } catch (_) {
        /* best-effort — focus without caret placement is still usable */
    }
}

function estimateInputCaretIndex(el, clientX, value) {
    const rect = el.getBoundingClientRect();
    const style = window.getComputedStyle(el);
    const paddingLeft = parseFloat(style.paddingLeft || "0") || 0;
    const paddingRight = parseFloat(style.paddingRight || "0") || 0;
    const contentLeft = paddingLeft;
    const contentRight = Math.max(contentLeft, rect.width - paddingRight);
    const contentWidth = Math.max(0, contentRight - contentLeft);
    const textWidth = measureInputText(style.font, value);
    const align = (style.textAlign || "left").toLowerCase();

    let textStart = contentLeft;
    if (align === "right" || align === "end") {
        textStart = contentRight - textWidth;
    } else if (align === "center") {
        textStart = contentLeft + (contentWidth - textWidth) / 2;
    }

    const x = clientX - rect.left;
    if (x <= textStart) return 0;
    if (x >= textStart + textWidth) return value.length;

    let bestIndex = 0;
    let bestDistance = Number.POSITIVE_INFINITY;
    for (let i = 0; i <= value.length; i++) {
        const caretX = textStart + measureInputText(style.font, value.slice(0, i));
        const distance = Math.abs(x - caretX);
        if (distance < bestDistance) {
            bestDistance = distance;
            bestIndex = i;
        }
    }

    return bestIndex;
}

function measureInputText(font, text) {
    caretMeasureCanvas ||= document.createElement("canvas");
    const ctx = caretMeasureCanvas.getContext("2d");
    if (!ctx) return text.length * 7;
    ctx.font = font || "11px sans-serif";
    return ctx.measureText(text).width;
}

export function downloadFile(fileName, base64Content, mimeType) {
    const byteCharacters = atob(base64Content);
    const byteNumbers = new Array(byteCharacters.length);
    for (let i = 0; i < byteCharacters.length; i++) {
        byteNumbers[i] = byteCharacters.charCodeAt(i);
    }

    const blob = new Blob([new Uint8Array(byteNumbers)], { type: mimeType });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = fileName;
    document.body.appendChild(anchor);
    anchor.click();
    document.body.removeChild(anchor);
    URL.revokeObjectURL(url);
}

export async function saveFile(fileName, base64Content, mimeType) {
    const byteCharacters = atob(base64Content);
    const bytes = new Uint8Array(byteCharacters.length);
    for (let i = 0; i < byteCharacters.length; i++) {
        bytes[i] = byteCharacters.charCodeAt(i);
    }

    if (window.showSaveFilePicker) {
        try {
            const extension = (String(fileName).match(/\.[^.]+$/) || [".bin"])[0];
            const handle = await window.showSaveFilePicker({
                suggestedName: fileName,
                types: [{ description: "File", accept: { [mimeType || "application/octet-stream"]: [extension] } }]
            });
            const writable = await handle.createWritable();
            await writable.write(bytes);
            await writable.close();
            return "saved";
        } catch (err) {
            if (err && err.name === "AbortError") {
                return "cancelled";
            }
        }
    }

    const blob = new Blob([bytes], { type: mimeType });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = fileName;
    document.body.appendChild(anchor);
    anchor.click();
    document.body.removeChild(anchor);
    URL.revokeObjectURL(url);
    return "downloaded";
}

export function openPrintableHtml(base64HtmlContent) {
    const htmlContent = atob(base64HtmlContent);
    const printWindow = window.open("", "_blank");
    if (!printWindow) return;

    printWindow.document.write(htmlContent);
    printWindow.document.close();
    printWindow.onload = () => printWindow.print();
}

/**
 * Custom row-windowing scroll reader. GridControl renders only the visible row
 * slice plus overscan; this reports the scroll container's scrollTop/clientHeight
 * back to C# (OnGridWindowScrollAsync) so it can recompute which slice to render.
 *
 * Reading scrollTop/clientHeight is pure geometry with no Blazor equivalent — the
 * one browser dependency of the windowing feature. The listener is rAF-throttled,
 * so at most one round-trip per animation frame regardless of scroll velocity.
 * An initial sync fires immediately so the window is right-sized to the real
 * viewport height on the first paint (before the user scrolls).
 */
export function registerGridWindowScroll(scrollEl, dotNetRef) {
    if (!scrollEl || !dotNetRef) return;

    // Idempotent: drop any prior listener on this element before re-binding.
    unregisterGridWindowScroll(scrollEl);

    let scheduled = false;
    const fire = () => {
        scheduled = false;
        dotNetRef.invokeMethodAsync("OnGridWindowScrollAsync", scrollEl.scrollTop, scrollEl.clientHeight)
            .catch(() => { /* best-effort — circuit may be tearing down */ });
    };
    const onScroll = () => {
        if (scheduled) return;
        scheduled = true;
        // rAF is the smooth, battery-friendly throttle for a visible tab, but the
        // browser PAUSES it while the tab is hidden/backgrounded — which would
        // freeze the row window on a programmatic scroll. Fall back to a coarse
        // timer when hidden so the window still tracks.
        if (typeof document !== "undefined" && document.hidden) {
            setTimeout(fire, 32);
        } else {
            requestAnimationFrame(fire);
        }
    };

    scrollEl.__gridWindowScroll = onScroll;
    scrollEl.addEventListener("scroll", onScroll, { passive: true });

    // Initial window sync — viewport height is known now that we're in the DOM.
    dotNetRef.invokeMethodAsync("OnGridWindowScrollAsync", scrollEl.scrollTop, scrollEl.clientHeight)
        .catch(() => { /* best-effort */ });
}

// Scroll the row-windowing container to an absolute pixel offset. Used to jump
// the virtualized viewport to a row that isn't currently rendered (type-search
// hit, Home/End, PageDown) — C# has already moved the window to include that
// row; this keeps the scrollbar position in sync. Pure geometry.
export function setGridScrollTop(scrollEl, top) {
    if (!scrollEl) return;
    const max = Math.max(0, scrollEl.scrollHeight - scrollEl.clientHeight);
    scrollEl.scrollTop = Math.max(0, Math.min(top || 0, max));
}

export function unregisterGridWindowScroll(scrollEl) {
    if (scrollEl && scrollEl.__gridWindowScroll) {
        scrollEl.removeEventListener("scroll", scrollEl.__gridWindowScroll);
        scrollEl.__gridWindowScroll = null;
    }
}

export function positionDatePickerDropdown(hostEl, dropdownEl) {
    if (!hostEl || !dropdownEl) return;

    const margin = 4;
    const hostRect = hostEl.getBoundingClientRect();
    const width = dropdownEl.offsetWidth || 184;
    const height = dropdownEl.offsetHeight || 154;
    const alignRight = hostEl.classList && hostEl.classList.contains("fx-datepicker-align-right");

    let left = alignRight ? hostRect.right - width : hostRect.left;
    left = Math.min(left, window.innerWidth - width - margin);
    left = Math.max(margin, left);

    let top = hostRect.bottom + 2;
    const topWhenFlipped = hostRect.top - height - 2;
    if (top + height > window.innerHeight - margin && topWhenFlipped >= margin) {
        top = topWhenFlipped;
    } else if (top + height > window.innerHeight - margin) {
        top = Math.max(margin, window.innerHeight - height - margin);
    }

    dropdownEl.classList.add("fx-datepicker-floating");
    dropdownEl.style.position = "fixed";
    dropdownEl.style.left = `${Math.round(left)}px`;
    dropdownEl.style.top = `${Math.round(top)}px`;
    dropdownEl.style.right = "auto";
    dropdownEl.style.bottom = "auto";
}

/**
 * Best-fit measurement: the width a column's content actually needs.
 *
 * Measured with a Range over each cell's contents, NOT scrollWidth — scrollWidth
 * is floored at clientWidth, so a column that has been dragged wide always reports
 * its dragged width and best-fit could never shrink it. A Range reports the laid-out
 * text width independently of the box it sits in, so widening then best-fitting
 * returns to the same number.
 *
 * Rows are sampled around the scroll viewport so one long value far off-screen
 * cannot stretch the column. Returns { field: px } for the fields it could measure;
 * the caller falls back to its own estimate for anything missing.
 */
export function measureColumnContentWidths(gridRoot, fields, sampleSize) {
    if (!gridRoot || !fields || !fields.length) return null;

    const wanted = new Set(fields);
    const limit = Math.max(1, Number(sampleSize) || 50);
    const out = {};

    // Cells clip with overflow:hidden and many use flex templates, so their
    // rendered width is whatever the current column allows — measuring that can
    // never grow a column back. Clone into an off-screen host laid out at
    // max-content to get the width the cell actually wants.
    const host = document.createElement("div");
    host.setAttribute("aria-hidden", "true");
    host.style.cssText = "position:absolute;left:-99999px;top:0;visibility:hidden;" +
                         "pointer-events:none;white-space:nowrap;width:max-content;";
    gridRoot.appendChild(host);

    const intrinsic = (el) => {
        if (!el) return 0;
        const cs = getComputedStyle(el);
        host.style.font = cs.font;
        host.style.letterSpacing = cs.letterSpacing;
        host.innerHTML = el.innerHTML;
        const w = host.getBoundingClientRect().width;
        host.innerHTML = "";
        const pad = (parseFloat(cs.paddingLeft) || 0) + (parseFloat(cs.paddingRight) || 0)
                  + (parseFloat(cs.borderLeftWidth) || 0) + (parseFloat(cs.borderRightWidth) || 0);
        return w + pad;
    };

    const bump = (field, w) => {
        if (!isFinite(w) || w <= 0) return;
        if (!(field in out) || w > out[field]) out[field] = w;
    };

    for (const th of gridRoot.querySelectorAll("thead th[data-field]")) {
        const field = th.getAttribute("data-field");
        if (!wanted.has(field)) continue;
        const label = th.querySelector(".fx-header-text") || th;
        const icons = th.querySelectorAll(".fx-sort-icon, .fx-filter-icon, .fx-filter-applied-mark").length * 16;
        bump(field, intrinsic(label) + icons + 18);
    }

    const rows = [...gridRoot.querySelectorAll("tbody tr")];
    if (rows.length) {
        const scroller = gridRoot.querySelector(".fx-grid-content") || gridRoot;
        const scRect = scroller.getBoundingClientRect();
        let first = 0;
        for (let i = 0; i < rows.length; i++) {
            if (rows[i].getBoundingClientRect().bottom > scRect.top) { first = i; break; }
        }
        const start = Math.max(0, first - Math.floor(limit / 4));
        const end = Math.min(rows.length, start + limit);

        // Measuring every sampled cell would mean hundreds of clones, so per column
        // only the few longest candidates are laid out.
        const byField = new Map();
        for (let i = start; i < end; i++) {
            for (const td of rows[i].querySelectorAll("td[data-field]")) {
                const field = td.getAttribute("data-field");
                if (!wanted.has(field)) continue;
                if (!byField.has(field)) byField.set(field, []);
                byField.get(field).push(td);
            }
        }
        for (const [field, cells] of byField) {
            cells.sort((a, b) => (b.textContent || "").length - (a.textContent || "").length);
            for (const td of cells.slice(0, 3)) bump(field, intrinsic(td) + 6);
        }
    }

    host.remove();

    const scroller = gridRoot.querySelector(".fx-grid-content") || gridRoot;
    if (scroller && scroller.clientWidth > 0) out["__fxContainerWidth"] = scroller.clientWidth;

    return Object.keys(out).length ? out : null;
}

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

export function measureGridAvailableWidth(gridRoot) {
    if (!gridRoot) return 0;
    const content = gridRoot.querySelector(".fx-grid-content");
    const vScrollbar = content ? (content.offsetWidth - content.clientWidth) : 0;
    return Math.max(0, gridRoot.clientWidth - vScrollbar - 2);
}

// ── Endpoint-only drag selection ─────────────────────────────────────────
// Pointer tracking stays in the browser for the WHOLE drag: the preview is
// painted client-side from each row's data-ari (absolute visible-row index)
// and the server hears ONE EndDragSelectionFromBrowserAsync(mode, finalIdx,
// moved) on release — one range computation, one SelectionChanged, one
// render. The preview is cleared only after that authoritative render
// (clearGridDragPreview is invoked from OnAfterRenderAsync), so there is no
// unselected flash. The anchor row is highlighted immediately on mousedown,
// which also gives plain clicks instant feedback before the round trip.
const gridDragSelectionBindings = new WeakMap();

function gridRowsWithAri(gridRoot) {
    return [...gridRoot.querySelectorAll("tbody tr.fx-row[data-ari]")];
}

// The selected look is painted by an inline style the SERVER writes on
// selected rows (grid cells built by RenderTreeBuilder carry no CSS scope
// attribute, so scoped class rules cannot reach them). The client preview
// must therefore paint the same way: an inline background. Rows that are
// genuinely selected keep their server-written style on clear.
// Every element the preview paints is tracked here, because Blazor's next
// render can rewrite a row's class attribute (adding fx-selected) and wipe
// the fx-drag-preview marker — a class-based sweep would then miss the row
// and its JS cell paint would stick (visible after keyboard navigation
// moves the selection away).
const paintedPreviewEls = new Set();

function muteSelectedLook(el) {
    el.dataset.fxMuted = "1";
    el.style.setProperty("background-color", "transparent", "important");
    el.style.setProperty("box-shadow", "none", "important");
    el.style.setProperty("outline", "none", "important");
    paintedPreviewEls.add(el);
}

function unmuteSelectedLook(el) {
    delete el.dataset.fxMuted;
    el.style.removeProperty("background-color");
    el.style.removeProperty("box-shadow");
    el.style.removeProperty("outline");
}

// A muted row that must go back to LOOKING selected: the row stayed inside
// the selection across the round trip, so Blazor emitted NO patch for it and
// nothing server-side will repaint it. Repaint from the same CSS variable the
// server's inline background uses; the next real state change rewrites the
// whole style attribute and discards this.
function restoreSelectedLook(el) {
    delete el.dataset.fxMuted;
    const v = getComputedStyle(el).getPropertyValue("--fx-grid-selected-row-bg").trim();
    el.style.setProperty("background-color", v || "#b6c8dd");
    el.style.removeProperty("box-shadow");
    el.style.removeProperty("outline");
}

function gridCellPreviewColor(gridRoot) {
    // Match the CLASS color multi-selected cells get after the server render
    // (.fx-cell-selected:not(.fx-cell-active) — the grey row-shade), so the
    // drag preview and the final selection are the same color.
    const v = getComputedStyle(gridRoot).getPropertyValue("--fx-grid-cell-selected-row-bg").trim();
    return v || "#e8e8e8";
}

function gridPreviewColor(gridRoot) {
    const v = getComputedStyle(gridRoot).getPropertyValue("--fx-grid-selected-row-bg").trim();
    return v || "#b6c8dd";
}

function setRowPreview(tr, on, color) {
    tr.classList.toggle("fx-drag-preview", on);
    // Paint the CELLS as well as the row: some grids render opaque td
    // backgrounds (the picklist), so a row-level color never shows through.
    if (on) {
        if (tr.dataset.fxMuted) tr.style.setProperty("background-color", color, "important");
        else tr.style.backgroundColor = color;
        paintedPreviewEls.add(tr);
        for (const td of tr.children) {
            if (td.dataset.fxMuted) td.style.setProperty("background-color", color, "important");
            else td.style.backgroundColor = color;
            paintedPreviewEls.add(td);
        }
    }
    else {
        // Press-muted rows leaving the drag range KEEP the mute (and stay in
        // the registry) — un-muting here would resurrect the old selection
        // mid-drag; the render-ack sweep settles them. Everything else:
        // cell paints are JS-owned (the server never writes td backgrounds),
        // so they must ALWAYS be cleared at handoff — a still-selected row
        // keeps them only until keyboard navigation unselects it server-side,
        // which rewrites the tr style but has no reason to touch the tds,
        // leaving a stuck blue row. The tr background is server-owned for
        // selected rows, so it keeps the fx-selected guard.
        if (tr.dataset.fxMuted) tr.style.setProperty("background-color", "transparent", "important");
        else if (!tr.classList.contains("fx-selected")) { tr.style.backgroundColor = ""; paintedPreviewEls.delete(tr); }
        for (const td of tr.children) {
            if (td.dataset.fxMuted) td.style.setProperty("background-color", "transparent", "important");
            else { td.style.backgroundColor = ""; paintedPreviewEls.delete(td); }
        }
    }
}

function setCellPreview(td, on, color) {
    td.classList.toggle("fx-drag-preview-cell", on);
    if (on) { td.style.setProperty("background-color", color, "important"); paintedPreviewEls.add(td); }
    else if (td.dataset.fxMuted) {
        // Press-muted cell leaving the drag range: KEEP the mute — the drag
        // painter must not resurrect the old selection mid-drag; the
        // render-ack sweep unmutes once the server's new selection landed.
        td.style.setProperty("background-color", "transparent", "important");
    }
    else { unmuteSelectedLook(td); paintedPreviewEls.delete(td); }
}

// A plain press REPLACES the selection, so the old rows must stop looking
// selected in the same frame — waiting for the server render leaves two
// highlighted rows on a single-select grid for a whole round trip. The
// server's next render rewrites the real state either way.
function clearSelectedLook(gridRoot, exceptTr) {
    gridRoot.querySelectorAll("tbody tr.fx-row.fx-selected").forEach(r => {
        if (r === exceptTr) return;
        // Mute with INLINE styles only — never remove fx-selected: a row that
        // stays selected across the round trip produces NO Blazor patch (its
        // render output is unchanged), so a client-side class removal sticks
        // forever and the row shows as a white gap inside the new selection.
        muteSelectedLook(r);
        for (const td of r.children) muteSelectedLook(td);
    });
}

export function registerGridDragSelection(gridRoot, dotNetRef, mode, anchorIndex, anchorField) {
    if (!gridRoot || !dotNetRef) return;
    unregisterGridDragSelection(gridRoot);

    const doc = gridRoot.ownerDocument || document;
    let lastIdx = anchorIndex, moved = false, ended = false, raf = 0, pending = null;

    const previewColor = mode === "row" ? gridPreviewColor(gridRoot) : gridCellPreviewColor(gridRoot);
    const applyPreview = toIdx => {
        const a = Math.min(anchorIndex, toIdx), b = Math.max(anchorIndex, toIdx);
        for (const tr of gridRowsWithAri(gridRoot)) {
            const ari = +tr.getAttribute("data-ari");
            const inRange = ari >= a && ari <= b;
            if (mode === "row") {
                setRowPreview(tr, inRange, previewColor);
            } else {
                const td = tr.querySelector(`td[data-field="${CSS.escape(anchorField)}"]`);
                if (td) setCellPreview(td, inRange, previewColor);
            }
        }
    };

    const finish = () => {
        if (ended) return;
        ended = true;
        cleanup();
        // Preview stays painted — the server clears it after its render.
        dotNetRef.invokeMethodAsync("EndDragSelectionFromBrowserAsync", mode, lastIdx, moved)
            .catch(() => clearGridDragPreview(gridRoot));
        // Safety-net sweep: render ordering can leave the server-side clear
        // waiting on a render that never comes, and Blazor class-attribute
        // diffs can strip the preview marker — after the round trip has
        // certainly landed, wipe every tracked paint that isn't backed by a
        // real selection (cell paints always; they are JS-owned).
        const net = () => {
            if (gridDragSelectionBindings.has(gridRoot)) { setTimeout(net, 800); return; }
            sweepStalePreviewPaints(null);
        };
        setTimeout(net, 1500);
    };

    const onMove = e => {
        pending = e;
        if (raf) return;
        raf = requestAnimationFrame(() => {
            raf = 0;
            if (ended || !pending) return;
            const ev = pending; pending = null;
            if ((ev.buttons & 1) === 0) { finish(); return; }
            const el = doc.elementFromPoint(ev.clientX, ev.clientY);
            const tr = el && el.closest ? el.closest("tbody tr.fx-row") : null;
            if (!tr || !gridRoot.contains(tr) || !tr.hasAttribute("data-ari")) return;
            if (mode === "cell") {
                const td = el.closest("td");
                if (!td || td.getAttribute("data-field") !== anchorField) return;
            }
            const idx = +tr.getAttribute("data-ari");
            if (idx === lastIdx) return;
            // Entry dead-zone: 16px rows make bare edges hair-trigger — the
            // pointer must be a few px INTO the row before it joins the range,
            // otherwise grazing a boundary highlights one row too many.
            const rect = tr.getBoundingClientRect();
            if (idx > lastIdx && ev.clientY < rect.top + 3) return;
            if (idx < lastIdx && ev.clientY > rect.bottom - 3) return;
            lastIdx = idx;
            if (!moved && mode === "cell") {
                // First real move of a cell drag: the selection is being
                // REPLACED, so the previous cells must stop LOOKING selected
                // now. Mute with INLINE styles only — removing the server's
                // classes desyncs Blazor's diff (cells kept in the new range
                // are never re-written, leaving half-toggled artifacts). The
                // render-ack sweep clears these inline mutes.
                gridRoot.querySelectorAll("td.fx-cell-selected").forEach(td => muteSelectedLook(td));
                gridRoot.querySelectorAll("tr.fx-cell-row-selected").forEach(tr => {
                    muteSelectedLook(tr);
                    for (const c of tr.children) muteSelectedLook(c);
                });
            }
            moved = true;
            applyPreview(idx);
        });
    };
    const onUp = () => finish();
    const cleanup = () => {
        doc.removeEventListener("pointermove", onMove, true);
        doc.removeEventListener("pointerup", onUp, true);
        if (raf) cancelAnimationFrame(raf);
        gridDragSelectionBindings.delete(gridRoot);
    };

    doc.addEventListener("pointermove", onMove, true);
    doc.addEventListener("pointerup", onUp, true);
    gridDragSelectionBindings.set(gridRoot, { cleanup });
    // Row mode: paint the anchor immediately — that's the instant press
    // feedback. Cell mode: NO paint on a plain press; the preview appears only
    // once the pointer really drags (applyPreview from onMove). Painting the
    // anchor cell here put a foreign row-selection blue on every cell click.
    if (mode === "row") {
        clearSelectedLook(gridRoot, null);
        applyPreview(anchorIndex);
    }
}

export function unregisterGridDragSelection(gridRoot) {
    if (!gridRoot) return;
    const b = gridDragSelectionBindings.get(gridRoot);
    if (b) b.cleanup();
}

export function clearGridDragPreview(gridRoot) {
    if (!gridRoot) return;
    gridRoot.querySelectorAll(".fx-drag-preview").forEach(r => setRowPreview(r, false, ""));
    gridRoot.querySelectorAll(".fx-drag-preview-cell").forEach(td => setCellPreview(td, false, ""));
    // Registry sweep: rows whose preview class was wiped by a class-attribute
    // diff still carry JS paints — clear every tracked element in this grid.
    for (const el of [...paintedPreviewEls]) {
        if (!gridRoot.contains(el)) continue;
        const tr = el.tagName === "TR" ? el : el.closest("tr");
        if (el.tagName === "TR" && tr && tr.classList.contains("fx-selected")) {
            // Still-selected muted row: Blazor emitted no patch for it, so
            // the selected look must be repainted client-side.
            if (el.dataset.fxMuted) restoreSelectedLook(el);
            else { el.style.removeProperty("box-shadow"); el.style.removeProperty("outline"); }
            paintedPreviewEls.delete(el);
            continue;
        }
        unmuteSelectedLook(el);
        paintedPreviewEls.delete(el);
    }
}

// ── Instant row feedback ────────────────────────────────────────────────
// Standing per-grid binding (registered once at grid init): a primary-button
// pointerdown on a data row paints the row preview IMMEDIATELY, before any
// server round trip — so the row highlight always appears first and the
// active-cell cue (which needs a server render) follows. Modifier presses are
// skipped (Ctrl toggles OFF / Shift ranges — a plain preview would lie), as
// are presses on in-cell editors. The server's authoritative render replaces
// the preview through the normal drag/click handoff.
const gridInstantFeedbackBindings = new WeakMap();

// Clear every tracked preview paint that is no longer backed by a real
// (fx-selected) row — cell paints always, since the server never writes them.
// Shared by the instant-feedback press, the drag-end safety net, and
// clearGridDragPreview, so a paint can never outlive its selection no matter
// which binding created it.
function sweepStalePreviewPaints(exceptTr) {
    for (const el of [...paintedPreviewEls]) {
        if (el === exceptTr) continue;
        const tr = el.tagName === "TR" ? el : el.closest("tr");
        const keepTr = el.tagName === "TR" && tr && tr.classList.contains("fx-selected");
        if (keepTr) {
            if (el.dataset.fxMuted) restoreSelectedLook(el);
            else { el.style.removeProperty("box-shadow"); el.style.removeProperty("outline"); }
        }
        else unmuteSelectedLook(el);
        if (el !== exceptTr) paintedPreviewEls.delete(el);
    }
}

export function registerGridInstantSelectionFeedback(gridRoot, cellMode = false) {
    if (!gridRoot || gridInstantFeedbackBindings.has(gridRoot)) return;
    let netTimer = 0;
    const onDown = e => {
        if (e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey) return;
        const t = e.target;
        if (t.closest && t.closest("input, select, textarea, button")) return;
        const tr = t.closest ? t.closest("tbody tr.fx-row[data-ari]") : null;
        if (!tr || !gridRoot.contains(tr)) return;

        if (cellMode) {
            // The press REPLACES the cell selection: mute every old selected
            // look in the SAME frame (inline styles only — server classes are
            // never touched, so Blazor's diff stays coherent) and paint the
            // pressed cell with the cell-selection color.
            const td = t.closest ? t.closest("td") : null;
            // 'important' priority: the single-cell-batch selected-cell rules
            // are themselves !important, so a plain inline mute loses.
            gridRoot.querySelectorAll("td.fx-cell-selected").forEach(c => { if (c !== td) muteSelectedLook(c); });
            // The row shade paints EVERY td of the row directly (!important),
            // so a muted row must mute its cells too.
            gridRoot.querySelectorAll("tr.fx-cell-row-selected").forEach(r => {
                if (r === tr) return;
                muteSelectedLook(r);
                for (const c of r.children) { if (c !== td) muteSelectedLook(c); }
            });
            if (td && gridRoot.contains(td)) {
                td.style.setProperty("background-color", gridCellPreviewColor(gridRoot), "important");
                paintedPreviewEls.add(td);
            }
        } else {
            const color = gridPreviewColor(gridRoot);
            gridRoot.querySelectorAll(".fx-drag-preview").forEach(r => { if (r !== tr) setRowPreview(r, false, ""); });
            // Sweep BEFORE muting: the sweep restores still-selected muted
            // rows, so running it after clearSelectedLook would undo the
            // press-frame clear it just applied. It still wipes stale paints
            // whose fx-* marker a server render already rewrote.
            sweepStalePreviewPaints(tr);
            clearSelectedLook(gridRoot, tr);
            setRowPreview(tr, true, color);
        }

        // Self-healing for plain-click grids (no drag capture registered, so
        // the drag-end safety net never runs there): after the round trip has
        // landed, wipe whatever the server did not turn into a selection.
        clearTimeout(netTimer);
        const net = () => {
            // A live drag owns the paints — sweeping mid-drag un-mutes the old
            // selection and repaints it under the user's cursor. Defer.
            if (gridDragSelectionBindings.has(gridRoot)) { netTimer = setTimeout(net, 800); return; }
            sweepStalePreviewPaints(null);
        };
        netTimer = setTimeout(net, 1500);
    };
    gridRoot.addEventListener("pointerdown", onDown, true);
    gridInstantFeedbackBindings.set(gridRoot, () => {
        clearTimeout(netTimer);
        gridRoot.removeEventListener("pointerdown", onDown, true);
    });
}

export function unregisterGridInstantSelectionFeedback(gridRoot) {
    if (!gridRoot) return;
    const cleanup = gridInstantFeedbackBindings.get(gridRoot);
    if (cleanup) { cleanup(); gridInstantFeedbackBindings.delete(gridRoot); }
}


// Server-applied keystrokes during the editor's focus round-trip must reach
// the mounted UNCONTROLLED input too — its mount snapshot predates them, and
// the first native oninput would otherwise overwrite the bridged characters.
export function setBatchEditorValue(input, value) {
    if (!input) return;
    input.value = value ?? "";
    try { input.setSelectionRange(input.value.length, input.value.length); } catch { /* non-text input */ }
}
