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

function supersedeGridScrollReset(gridRoot) {
    const generation = (gridRoot.__gridScrollResetGeneration || 0) + 1;
    gridRoot.__gridScrollResetGeneration = generation;
    return generation;
}

export function resetInitialGridScroll(gridRoot) {
    if (!gridRoot) return;

    const doc = gridRoot.ownerDocument || document;
    const win = doc.defaultView || window;
    const generation = supersedeGridScrollReset(gridRoot);
    const resetIfCurrent = () => {
        if (gridRoot.__gridScrollResetGeneration === generation)
            resetGridScrollTargets(gridRoot);
    };
    const requestFrame = typeof win.requestAnimationFrame === "function"
        ? win.requestAnimationFrame.bind(win)
        : callback => win.setTimeout(callback, 0);

    resetIfCurrent();
    requestFrame(() => {
        resetIfCurrent();
        requestFrame(resetIfCurrent);
    });
    win.setTimeout(resetIfCurrent, 0);
    win.setTimeout(resetIfCurrent, 80);
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

    // Keep the edge being dragged inside the scroll viewport. Without this the column grows
    // under the splitter and the grip leaves the view, so the user is dragging something they
    // can no longer see.
    const keepGripVisible = (clientX) => {
        const scroller = gridRoot.querySelector(".fx-grid-content");
        if (!scroller || scroller.scrollWidth <= scroller.clientWidth) return;

        const box = scroller.getBoundingClientRect();
        const margin = 24;
        if (clientX > box.right - margin) {
            scroller.scrollLeft += clientX - (box.right - margin);
        } else if (clientX < box.left + margin) {
            scroller.scrollLeft -= (box.left + margin) - clientX;
        }
    };

    const onMouseMove = (event) => {
        event.preventDefault();
        keepGripVisible(event.clientX);
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
    const buffersEditMountTyping = gridRoot.dataset.fxClientBufferedEditing === "true";
    const bufferedEditEvents = new WeakSet();
    let pendingEditTyping = null;
    let pendingEditPump = 0;
    let lastPressedEditCell = null;

    const clearPendingEditTyping = () => {
        pendingEditTyping = null;
        if (pendingEditPump) {
            window.cancelAnimationFrame(pendingEditPump);
            pendingEditPump = 0;
        }
    };

    const cellIdentity = cell => ({
        row: cell?.closest("tr.fx-row")?.getAttribute("data-ari") ?? "",
        field: cell?.getAttribute("data-field") ?? ""
    });

    const resolveCell = identity => {
        if (!identity?.field) return null;
        const row = Array.from(gridRoot.querySelectorAll("tr.fx-row[data-ari]"))
            .find(candidate => candidate.getAttribute("data-ari") === identity.row);
        return row
            ? Array.from(row.cells).find(cell => cell.getAttribute("data-field") === identity.field) ?? null
            : null;
    };

    const findActiveEditableCell = () => {
        if (lastPressedEditCell && Date.now() - lastPressedEditCell.time < 2000) {
            const pressed = resolveCell(lastPressedEditCell.identity);
            if (pressed?.classList.contains("fx-cell-editable")) return pressed;
        }
        return gridRoot.querySelector("td.fx-cell-active.fx-cell-editable");
    };

    const sameCell = (cell, identity) => {
        const current = cellIdentity(cell);
        return current.row === identity.row && current.field === identity.field;
    };

    const deliverBufferedCommit = (input, commit, attempts = 0) => {
        if (!input?.isConnected) return;
        if (input.dataset.fxClientBufferedEditor !== "1" && attempts < 120) {
            window.requestAnimationFrame(() => deliverBufferedCommit(input, commit, attempts + 1));
            return;
        }

        input.dispatchEvent(new KeyboardEvent("keydown", {
            key: commit.key,
            shiftKey: commit.shiftKey,
            ctrlKey: commit.ctrlKey,
            altKey: commit.altKey,
            metaKey: commit.metaKey,
            bubbles: true,
            cancelable: true
        }));
    };

    const pumpPendingEditTyping = () => {
        pendingEditPump = 0;
        const pending = pendingEditTyping;
        if (!pending) return;
        if (Date.now() >= pending.expires) {
            clearPendingEditTyping();
            return;
        }

        const cell = findActiveEditableCell();
        const input = cell && sameCell(cell, pending.identity)
            ? cell.querySelector("input.fx-batch-input, textarea.fx-batch-input")
            : null;
        if (!input) {
            pendingEditPump = window.requestAnimationFrame(pumpPendingEditTyping);
            return;
        }

        input.value = pending.value;
        input.dataset.fxUserTyped = "1";
        try { input.focus({ preventScroll: true }); } catch { input.focus?.(); }
        try { input.setSelectionRange(input.value.length, input.value.length); } catch { }

        const commit = pending.commit;
        clearPendingEditTyping();
        if (commit)
            window.requestAnimationFrame(() => deliverBufferedCommit(input, commit));
    };

    const schedulePendingEditPump = () => {
        if (!pendingEditPump)
            pendingEditPump = window.requestAnimationFrame(pumpPendingEditTyping);
    };

    const bufferEditMountKey = event => {
        if (!buffersEditMountTyping || bufferedEditEvents.has(event)) return false;
        bufferedEditEvents.add(event);

        const target = event.target instanceof Element ? event.target : null;
        if (!target || !gridRoot.contains(target) || target.matches("input, textarea, select"))
            return false;
        if (event.altKey || event.ctrlKey || event.metaKey || event.isComposing)
            return false;

        const cell = findActiveEditableCell();
        if (!cell) {
            clearPendingEditTyping();
            return false;
        }

        const identity = cellIdentity(cell);
        const isCharacter = event.key.length === 1;
        if (isCharacter) {
            const startsBuffer = !pendingEditTyping || !sameCell(cell, pendingEditTyping.identity);
            if (startsBuffer) {
                pendingEditTyping = {
                    identity,
                    value: event.key,
                    commit: null,
                    expires: Date.now() + 4000
                };
                if (event.key === " ") event.preventDefault();
                schedulePendingEditPump();
                return false;
            }

            pendingEditTyping.value += event.key;
            pendingEditTyping.expires = Date.now() + 4000;
            event.preventDefault();
            event.stopImmediatePropagation();
            schedulePendingEditPump();
            return true;
        }

        if (!pendingEditTyping || !sameCell(cell, pendingEditTyping.identity))
            return false;

        if (event.key === "Backspace" || event.key === "Delete") {
            if (event.key === "Backspace")
                pendingEditTyping.value = pendingEditTyping.value.slice(0, -1);
            pendingEditTyping.expires = Date.now() + 4000;
            event.preventDefault();
            event.stopImmediatePropagation();
            schedulePendingEditPump();
            return true;
        }

        if (event.key === "Enter" || event.key === "NumpadEnter" || event.key === "Tab" || event.key === "Escape") {
            pendingEditTyping.commit = {
                key: event.key,
                shiftKey: event.shiftKey,
                ctrlKey: event.ctrlKey,
                altKey: event.altKey,
                metaKey: event.metaKey
            };
            event.preventDefault();
            event.stopImmediatePropagation();
            schedulePendingEditPump();
            return true;
        }

        return false;
    };

    const rememberPressedEditCell = event => {
        const target = event.target instanceof Element ? event.target : null;
        const cell = target?.closest?.("td.fx-cell-editable");
        if (!cell || !gridRoot.contains(cell)) return;
        lastPressedEditCell = { identity: cellIdentity(cell), time: Date.now() };
    };

    const onKeyDown = (event) => {
        if (!pendingEditTyping
            && (event.key === "Tab" || event.key.startsWith("Arrow")))
            lastPressedEditCell = null;
        if (bufferEditMountKey(event)) return;

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

        // A PageControl navigation map treats each grid as one page-level Tab
        // stop. Let its capture handler move between screen regions while the
        // grid continues to own arrow-key navigation within the active row.
        if (event.key === "Tab"
            && gridRoot.closest("[data-fx-page-tab-navigation='true']")) return;

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
    gridRoot.addEventListener("pointerdown", rememberPressedEditCell, true);
    gridKeyboardTrapBindings.set(gridRoot, { onKeyDown, doc, clearPendingEditTyping, rememberPressedEditCell });
}

export function unregisterGridKeyboardTrap(gridRoot) {
    if (!gridRoot) return;
    const handlers = gridKeyboardTrapBindings.get(gridRoot);
    if (!handlers) return;

    gridRoot.removeEventListener("keydown", handlers.onKeyDown, true);
    handlers.doc?.removeEventListener?.("keydown", handlers.onKeyDown, true);
    gridRoot.removeEventListener("pointerdown", handlers.rememberPressedEditCell, true);
    handlers.clearPendingEditTyping?.();
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

/**
 * First VISIBLE pixel row inside a vertical scroll container.
 *
 * The column header is `position: sticky; top: 0` INSIDE the scroll container, so
 * it paints OVER the first rows: the container's own top edge is not where the
 * user starts seeing rows. Measuring against that edge makes a row that has
 * slipped under the header test as "already visible", so arrowing up past the
 * first visible row does not scroll at all — the selection lands on a row hidden
 * behind the header, and only the NEXT arrow-up scrolls (by then one row late,
 * with the selected row still occluded). Treat the sticky header's bottom edge as
 * the top of the visible area instead.
 */
function getGridVisibleTop(bodyViewportEl, viewportTop) {
    let visibleTop = viewportTop;
    const headers = bodyViewportEl.querySelectorAll(".fx-grid-header-viewport, .fx-grid-header");
    for (const header of headers) {
        let position;
        try {
            position = getComputedStyle(header).position;
        } catch (_) {
            continue;
        }
        // Only a header PINNED to the top occludes rows. A header that scrolls
        // away with the body (or one laid out above the scroll container) does not.
        if (position !== "sticky") continue;

        const headerRect = header.getBoundingClientRect();
        if (headerRect.height <= 0) continue;
        if (headerRect.top > viewportTop + 1) continue;
        if (headerRect.bottom > visibleTop) visibleTop = headerRect.bottom;
    }
    return visibleTop;
}

/**
 * Hide the row left straddling the first visible pixel row.
 *
 * The viewport height is never a whole number of rows, so after a scroll
 * correction the row crossing the header's bottom edge is painted as a 1-3px
 * sliver of clipped text jammed against the header -- unreadable, and it reads
 * as the header being dirty. Nudge the scroll down by exactly that sliver so
 * the first row below the header is always a WHOLE row.
 *
 * This never scrolls the active row out of view: at the point this runs the
 * active row is flush against one edge, and hiding the straddler only moves
 * content up. The row carrying the active cell is skipped outright.
 */
function snapGridTopToWholeRow(bodyViewportEl, activeCell) {
    const visibleTop = getGridVisibleTop(
        bodyViewportEl, bodyViewportEl.getBoundingClientRect().top);

    for (const row of bodyViewportEl.querySelectorAll("tbody > tr")) {
        if (row.classList.contains("fx-grid-window-spacer")) continue;

        const rect = row.getBoundingClientRect();
        if (rect.height <= 0) continue;
        // Not the straddler: entirely below the fold, or entirely behind the header.
        if (rect.top >= visibleTop - 0.5 || rect.bottom <= visibleTop + 0.5) continue;

        // Never hide the row the user is standing on.
        if (activeCell && row.contains(activeCell)) return;

        const maxScrollTop = Math.max(
            0, bodyViewportEl.scrollHeight - bodyViewportEl.clientHeight);
        bodyViewportEl.scrollTop = clamp(
            bodyViewportEl.scrollTop + (rect.bottom - visibleTop), 0, maxScrollTop);
        return;
    }
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
        // NOT outerBodyRect.top — the sticky header covers the first rows.
        top: getGridVisibleTop(bodyViewportEl, outerBodyRect.top),
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

    // Vertically, land the active row FLUSH against the sticky header (or the
    // bottom edge) -- deliberately NOT `padding` px clear of it the way the
    // horizontal pass does. A vertical gap is not empty space: it exposes a
    // sliver of the NEIGHBOURING row, which looks like a squashed extra line of
    // text against the header. The epsilon only absorbs sub-pixel rounding, so
    // a row already flush does not re-trigger a scroll on every keystroke.
    const maxScrollTop = Math.max(0, bodyViewportEl.scrollHeight - bodyViewportEl.clientHeight);
    const edgeEpsilon = 0.5;

    if (cellRect.top < bodyRect.top - edgeEpsilon) {
        bodyViewportEl.scrollTop = clamp(
            bodyViewportEl.scrollTop - (bodyRect.top - cellRect.top), 0, maxScrollTop);
        snapGridTopToWholeRow(bodyViewportEl, activeCell);
    } else if (cellRect.bottom > bodyRect.bottom + edgeEpsilon) {
        bodyViewportEl.scrollTop = clamp(
            bodyViewportEl.scrollTop + (cellRect.bottom - bodyRect.bottom), 0, maxScrollTop);
        snapGridTopToWholeRow(bodyViewportEl, activeCell);
    }
}

// ── Active-cell pre-paint viewport sync ──────────────────────────────────────

const activeCellSyncBindings = new WeakMap();

/**
 * Land the keyboard-navigation scroll in the SAME painted frame as the
 * highlight move.
 *
 * Without this, arrowing through rows painted in TWO steps: Blazor's DOM patch
 * moved .fx-cell-active while the grid still sat at the OLD scroll offset — the
 * browser happily painted that frame — and only the post-render interop call
 * scrolled the row into view, one frame later. One row at a time that is a
 * visible one-row-pitch jump right after the highlight moves: the per-keystroke
 * flicker. (Mouse-wheel scrolling never enters this path, which is why it does
 * not flicker.)
 *
 * A MutationObserver callback runs as a microtask BEFORE the browser's next
 * rendering opportunity, so correcting the scroll here is the one place both
 * changes can be composed into a single paint. Server-side C# cannot do this:
 * by the time its post-render interop call arrives, the stale frame is already
 * on screen. That late call is kept as the fallback — when the cell is already
 * flush it early-outs inside the edge epsilon, so it never double-scrolls.
 *
 * Scroll-hijack guard: correct ONLY when the active cell moved to a DIFFERENT
 * element (a class mutation on a cell it was not on before). Re-mutations of
 * the same cell's class and structural patches (row windowing re-renders while
 * the user has wheel-scrolled away from the active row) must NOT yank the
 * viewport back to the active cell.
 */
export function registerActiveCellScrollSync(gridRoot) {
    if (!gridRoot || activeCellSyncBindings.has(gridRoot)) return;

    let lastActiveCell = gridRoot.querySelector(".fx-cell-active");
    let lastRowRevealToken = null;

    const revealRequestedRow = () => {
        const token = gridRoot.getAttribute("data-fx-row-reveal-token");
        const rawIndex = gridRoot.getAttribute("data-fx-row-reveal-index");
        if (!token || token === lastRowRevealToken || rawIndex === null) return false;

        const rowIndex = Number.parseInt(rawIndex, 10);
        if (!Number.isFinite(rowIndex)) return false;
        if (!gridRoot.querySelector(`tr.fx-row[data-ari="${rowIndex}"]`)) return false;

        scrollSelectedGridRowToTop(gridRoot, rowIndex, 0, true);
        lastRowRevealToken = token;
        return true;
    };

    const observer = new MutationObserver(mutations => {
        // Blazor applies the row window, selected class, and reveal token in one
        // DOM batch. MutationObserver runs before paint, so align that completed
        // batch now instead of showing an unselected scroll first.
        if (revealRequestedRow()) return;

        for (const mutation of mutations) {
            if (mutation.type !== "attributes") continue;
            const el = mutation.target;
            if (!el.classList || !el.classList.contains("fx-cell-active")) continue;
            if (el === lastActiveCell) return; // same cell re-styled, not a move
            lastActiveCell = el;
            ensureActiveGridCellVisible(gridRoot);
            return;
        }
    });

    observer.observe(gridRoot, {
        subtree: true,
        attributes: true,
        attributeFilter: ["class", "data-fx-row-reveal-index", "data-fx-row-reveal-token"],
        childList: true
    });
    revealRequestedRow();
    activeCellSyncBindings.set(gridRoot, observer);
}

export function unregisterActiveCellScrollSync(gridRoot) {
    if (!gridRoot) return;
    const observer = activeCellSyncBindings.get(gridRoot);
    if (!observer) return;
    observer.disconnect();
    activeCellSyncBindings.delete(gridRoot);
}

/**
 * Measure the three geometry facts neither CSS nor C# can derive for itself, and
 * publish the header height back into CSS.
 *
 * WHY THIS NEEDS THE DOM (minimise-JS rule): a row's rendered pitch is the sum of
 * font metrics, line-height, cell padding and collapsed borders, and the sticky
 * header's height varies with theme and font. Neither is knowable server-side, and
 * CSS cannot read one element's height into a custom property. Every consumer
 * degrades to its previous hardcoded constant if this never runs.
 *
 * Returns { headerPx, rowPx, viewportPx }; rowPx is 0 when no data rows are
 * rendered yet, which the caller treats as "not measured, try again".
 */
export function measureGridMetrics(gridRoot) {
    if (!gridRoot) return null;

    const bodyViewportEl = getGridVerticalViewportElement(gridRoot);
    if (!bodyViewportEl) return null;

    // SETTLED-LAYOUT GATE. A grid can render rows before it has been given its final
    // box — behind a modal, inside a pane that has not been sized yet — and in that
    // state both the row pitch and the header height are wrong (measured 16.5px in a
    // transient layout where the settled values were 14px and 16px). The tell is that
    // the scrollport is taller than the grid that contains it, which a settled layout
    // can never be. Report "not measurable" so the caller retries on a later render
    // instead of locking in a transient reading for the lifetime of the grid.
    const rootHeight = gridRoot.getBoundingClientRect().height;
    if (rootHeight <= 0 || bodyViewportEl.clientHeight > rootHeight + 1) {
        return { headerPx: 0, rowPx: 0, viewportPx: 0 };
    }

    // How much of the scrollport's top the header occupies ONCE PINNED — which is its
    // own height plus its `top` offset, NOT getGridVisibleTop()'s bottom-minus-top.
    // Those differ before the first scroll: at scrollTop 0 the header still sits in
    // normal flow, half a pixel below the padding edge (the table's collapsed border),
    // and measuring its bottom would bake that 0.5px into a value that has to stay
    // correct for every later scroll position.
    let headerPx = 0;
    for (const header of bodyViewportEl.querySelectorAll(".fx-grid-header-viewport, .fx-grid-header")) {
        let style;
        try {
            style = getComputedStyle(header);
        } catch (_) {
            continue;
        }
        if (style.position !== "sticky") continue;

        const rect = header.getBoundingClientRect();
        if (rect.height <= 0) continue;

        const stickyOffset = parseFloat(style.top);
        const occupies = rect.height + (Number.isFinite(stickyOffset) ? stickyOffset : 0);
        if (occupies > headerPx) headerPx = occupies;
    }

    // Row PITCH, not one row's height: with border-collapse the shared border is
    // owned by the table, so consecutive row tops are the only honest measure.
    const tops = [];
    for (const row of bodyViewportEl.querySelectorAll("tbody > tr")) {
        if (row.classList.contains("fx-grid-window-spacer")) continue;
        const rect = row.getBoundingClientRect();
        if (rect.height <= 0) continue;
        tops.push(rect.top);
        if (tops.length >= 8) break;
    }

    let rowPx = 0;
    if (tops.length >= 2) {
        const gaps = [];
        for (let i = 1; i < tops.length; i++) gaps.push(tops[i] - tops[i - 1]);
        // Median, so one odd row (inline edit row, a user-resized row) cannot skew it.
        gaps.sort((a, b) => a - b);
        rowPx = gaps[Math.floor(gaps.length / 2)];
    }

    // Hand the header height to CSS so `scroll-padding-top` keeps the browser's OWN
    // scroll-into-view (focus(), scrollIntoView()) from parking a row under the header.
    bodyViewportEl.style.setProperty("--fx-grid-header-h", `${headerPx}px`);

    return { headerPx, rowPx: rowPx > 0 ? rowPx : 0, viewportPx: bodyViewportEl.clientHeight };
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
        if (el.dataset.fxUserTyped === "1"
            || ("value" in el && el.value !== (el.getAttribute("value") ?? ""))) {
            return;
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

        if (el.dataset.fxUserTyped === "1"
            || el.value !== (el.getAttribute("value") ?? "")) {
            return;
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
            // LATCH GUARD: if the tab is hidden between scheduling and painting,
            // that rAF callback never runs — `scheduled` would stay true and this
            // listener would ignore every later scroll, leaving the row window
            // frozen (the grid then shows spacer, i.e. blank, for the rest of the
            // session). A backstop timer runs the same work if rAF did not.
            setTimeout(() => { if (scheduled) fire(); }, 250);
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
// Height of one rendered group-header row — grouped row windowing sizes its
// header entries from this one-time measurement (headers are taller than data
// rows, and the spacer math needs the real pitch). Pure geometry.
export function measureGridGroupHeaderHeight(scrollEl) {
    const r = scrollEl && scrollEl.querySelector("tr.fx-group-header-row");
    return r ? r.getBoundingClientRect().height : 0;
}

export function setGridScrollTop(scrollEl, top) {
    if (!scrollEl) return;
    const max = Math.max(0, scrollEl.scrollHeight - scrollEl.clientHeight);
    scrollEl.scrollTop = Math.max(0, Math.min(top || 0, max));
}

// Reveal the requested selected row directly below the sticky header. An
// already-visible row can stay in place; the fallback offset mounts its
// row-window slice when that exact row is not in the DOM yet.
export function scrollSelectedGridRowToTop(gridRoot, rowIndex, fallbackTop, onlyIfNeeded = false) {
    if (!gridRoot) return false;

    // An explicit reveal wins over delayed first-paint reset callbacks.
    supersedeGridScrollReset(gridRoot);

    const scrollEl = getGridVerticalViewportElement(gridRoot);
    if (!scrollEl) return false;

    const selectedRow = gridRoot.querySelector(
        `tr.fx-row[data-ari="${rowIndex}"]`);
    if (!selectedRow) {
        setGridScrollTop(scrollEl, fallbackTop);
        return false;
    }

    const viewportRect = scrollEl.getBoundingClientRect();
    const visibleTop = getGridVisibleTop(scrollEl, viewportRect.top);
    const rowRect = selectedRow.getBoundingClientRect();
    const edgeEpsilon = 0.5;
    if (onlyIfNeeded
        && rowRect.top >= visibleTop - edgeEpsilon
        && rowRect.bottom <= viewportRect.bottom + edgeEpsilon) {
        return true;
    }

    const max = Math.max(0, scrollEl.scrollHeight - scrollEl.clientHeight);
    const target = scrollEl.scrollTop + rowRect.top - visibleTop;
    scrollEl.scrollTop = Math.max(0, Math.min(target, max));
    return true;
}

export function unregisterGridWindowScroll(scrollEl) {
    if (scrollEl && scrollEl.__gridWindowScroll) {
        scrollEl.removeEventListener("scroll", scrollEl.__gridWindowScroll);
        scrollEl.__gridWindowScroll = null;
    }
}

export function positionDatePickerDropdown(hostEl, dropdownEl, popupLayerEl) {
    if (!hostEl || !dropdownEl) return;

    // A manual Popover is painted in the browser's top layer, outside every
    // grid/table/overflow stacking context, without physically moving the
    // Blazor-owned nodes. Older browsers simply retain the fixed-position
    // fallback below.
    if (popupLayerEl && typeof popupLayerEl.showPopover === "function") {
        try {
            if (!popupLayerEl.matches(":popover-open")) {
                popupLayerEl.showPopover();
            }
        } catch {
            // Keep the existing fixed-position fallback.
        }
    }

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

    if (mode === "menu") {
        requestAnimationFrame(() => menuEl?.focus({ preventScroll: true }));
        return true;
    }

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

// Popup openers the library itself renders, plus the ARIA declaration any host
// template can carry. A button with none of these is not a popup opener.
const CELL_POPUP_OPENERS =
    ".fx-cell-edit-btn, .fx-cell-action-btn, .fx-grid-popup-btn, [aria-haspopup]:not([aria-haspopup='false'])";

function isActivatableCellPopup(el) {
    if (el.disabled === true) return false;
    if (el.matches("[disabled], [hidden], [aria-disabled='true'], [aria-hidden='true'], [data-fx-no-enter]")) return false;
    if (el.closest("[aria-hidden='true'], [data-fx-no-enter]")) return false;
    const style = window.getComputedStyle(el);
    return style.display !== "none"
        && style.visibility !== "hidden"
        && Number.parseFloat(style.opacity) !== 0;
}

// The fx-cell-active class is written by server renders only, and the client
// navigation preview moves the server's active cell WITHOUT one — so the
// caller's row/field identity resolves the cell whenever the row carries an
// absolute index. Grouped rows have none and pass ari < 0.
function resolveCellForPopupActivation(gridRoot, ari, field) {
    if (Number.isInteger(ari) && ari >= 0) {
        const row = gridRoot.querySelector(`tbody tr.fx-row[data-ari="${ari}"]`);
        if (!row || row.closest(".fx-grid") !== gridRoot) return null;
        return Array.from(row.children).find(td => td.getAttribute("data-field") === field) ?? null;
    }
    const active = Array.from(gridRoot.querySelectorAll("td.fx-cell-active"))
        .find(td => td.closest(".fx-grid") === gridRoot);
    return active && active.getAttribute("data-field") === field ? active : null;
}

/**
 * Enter on the active cell activates that cell's popup opener. A host cell
 * template is an opaque render fragment on the C# side, so the opener can only
 * be reached as a DOM click. The opener is never guessed: an explicit
 * data-fx-cell-popup marker is authoritative for the whole cell, and without
 * one only a declared popup affordance is activated.
 */
export function activateActiveCellPopup(gridRoot, ari, field) {
    if (!gridRoot || !field) return false;
    const cell = resolveCellForPopupActivation(gridRoot, ari, field);
    if (!cell) return false;

    const marked = Array.from(cell.querySelectorAll("[data-fx-cell-popup]"));
    if (marked.length) {
        const target = marked.find(isActivatableCellPopup);
        if (!target) return false;
        target.click();
        return true;
    }

    const opener = Array.from(cell.querySelectorAll(CELL_POPUP_OPENERS)).find(isActivatableCellPopup);
    if (!opener) return false;
    opener.click();
    return true;
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

// ── Selection-paint arbiter ─────────────────────────────────────────────
// ONE authority per grid over which client gesture currently owns the
// selection visuals. Every client paint system (drag preview, instant press
// feedback, keyboard navigation preview) enrolls its gesture here and every
// deferred sweep/expiry consults it, so no system can ever treat another
// system's LIVE gesture — or its own still-running one — as stale residue.
//  - generation/owner: monotonically increasing gesture id + which side
//    ("pointer"/"keyboard") started it. A sweep armed by gesture N is a
//    no-op once gesture N+1 exists.
//  - pointerDown: the PHYSICAL button state. A press is never stale while
//    the button is still down, no matter how old it is (the 1.5s safety
//    net used to fire mid-hold, un-muting the old selection under the
//    user's finger and clearing the press paint).
//  - cancelKeyboard: hand-off hook — a pointer gesture starting cancels a
//    live keyboard preview in the same frame (flushing its position first).
const gridPaintArbiters = new WeakMap();

function gridPaintArbiter(gridRoot) {
    let state = gridPaintArbiters.get(gridRoot);
    if (!state) {
        state = { generation: 0, owner: null, pointerDown: false, cancelKeyboard: null };
        gridPaintArbiters.set(gridRoot, state);
    }
    return state;
}

function beginGridPaintGesture(gridRoot, owner) {
    const state = gridPaintArbiter(gridRoot);
    // A new gesture invalidates the previous one's deferred sweeps (the
    // generation bump makes them no-ops) — so the battlefield must be swept
    // HERE, or paints whose only cleaner was that sweep are orphaned
    // forever. The trap: double-click on a NON-editable cell — no editor,
    // no state change, no render ever rewrites that row, and the press's
    // td-level inline paints (server never writes td backgrounds) survived
    // every later gesture as a permanently selected-looking row.
    if (state.generation > 0 && !state.pointerDown) {
        try { sweepStalePreviewPaints(null); } catch { /* best effort */ }
    }
    const generation = ++state.generation;
    if (owner === "pointer" && state.cancelKeyboard) state.cancelKeyboard();
    state.owner = owner;
    return generation;
}

function isCurrentGridPaintGesture(gridRoot, owner, generation) {
    const state = gridPaintArbiter(gridRoot);
    return state.owner === owner && state.generation === generation;
}

// True while a pointer gesture is PHYSICALLY live: button down, or the drag
// capture still registered. Sweeps must defer, never fire, while this holds.
function gridPointerGestureLive(gridRoot) {
    return gridPaintArbiter(gridRoot).pointerDown || gridDragSelectionBindings.has(gridRoot);
}

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

// Mute a previously-selected cell/row look in place. MUST be important-level
// inline styles: several selection rules carry !important, which plain
// inline styles lose to. The data-fx-muted marker keeps the drag painter
// from resurrecting a muted look mid-drag.
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

// The CLASS color multi-selected cells get after the server render — the
// cell drag preview uses it so preview and final selection match.
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
    // CELL-mode grids carry the selected look on TD classes, not
    // tr.fx-selected — without this the old selection survives a press or a
    // whole drag until the release render replaces it.
    gridRoot.querySelectorAll(
        "tbody td.fx-cell-row-selected, tbody td.fx-cell-selected, tbody td.fx-cell-active")
        .forEach(td => { if (td.closest("tr") !== exceptTr) muteSelectedLook(td); });
}

export function registerGridDragSelection(gridRoot, dotNetRef, mode, anchorIndex, anchorField) {
    if (!gridRoot || !dotNetRef) return;
    unregisterGridDragSelection(gridRoot);

    const doc = gridRoot.ownerDocument || document;
    let lastIdx = anchorIndex, moved = false, ended = false, raf = 0, pending = null;
    // The drag capture is registered by the server AFTER the press that the
    // instant-feedback binding already enrolled with the arbiter — adopt that
    // gesture rather than starting a new one.
    const gestureGeneration = gridPaintArbiter(gridRoot).generation;

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
            // A newer gesture owns the paints now — this net is obsolete.
            if (!isCurrentGridPaintGesture(gridRoot, "pointer", gestureGeneration)) return;
            // Never sweep while a pointer gesture is physically live.
            if (gridPointerGestureLive(gridRoot)) { setTimeout(net, 800); return; }
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

    // Immediate visual feedback the moment the drag capture arms (HHM-871
    // follow-up QA complaint): the old flow painted nothing until the pointer
    // crossed into ANOTHER row, so a cell-mode press + short drag looked dead
    // until the server's post-mouseup render delivered the whole range at
    // once. Paint the anchor cell now; the range then grows live under the
    // pointer, and the render-ack sweep clears the paint if the gesture ends
    // as a plain click that selects something else. Row-mode grids that
    // highlight whole rows already get their press paint from the
    // instant-feedback binding, so they are excluded.
    if (mode === "cell" || !gridHighlightsSelectedRows(gridRoot)) applyPreview(anchorIndex);
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
    const doc = gridRoot.ownerDocument || document;
    let netTimer = 0;
    const onDown = e => {
        if (e.button !== 0) return;
        // Track the PHYSICAL press for every primary-button pointerdown,
        // modifier presses included: while the button is down no sweep may
        // treat this gesture's paints/mutes as stale (a >1.5s hold used to
        // trip the safety net mid-press, re-tinting the old selection under
        // the user's finger and clearing the press paint).
        const arbiter = gridPaintArbiter(gridRoot);
        arbiter.pointerDown = true;
        const releasePointer = () => {
            arbiter.pointerDown = false;
            doc.removeEventListener("pointerup", releasePointer, true);
            doc.removeEventListener("pointercancel", releasePointer, true);
            window.removeEventListener("blur", releasePointer);
        };
        doc.addEventListener("pointerup", releasePointer, true);
        doc.addEventListener("pointercancel", releasePointer, true);
        window.addEventListener("blur", releasePointer);

        if (e.ctrlKey || e.metaKey || e.shiftKey) return;
        const t = e.target;
        if (t.closest && t.closest("input, select, textarea, button")) return;
        const tr = t.closest ? t.closest("tbody tr.fx-row[data-ari]") : null;
        if (!tr || !gridRoot.contains(tr)) return;
        // This press owns the selection visuals now: a live keyboard preview
        // is cancelled (its position flushed) inside beginGridPaintGesture.
        const gestureGeneration = beginGridPaintGesture(gridRoot, "pointer");

        if (cellMode) {
            // The press REPLACES the cell selection: mute every old selected
            // look in the SAME frame (inline styles only — server classes are
            // never touched, so Blazor's diff stays coherent) and paint the
            // pressed cell with the cell-selection color.
            const td = t.closest ? t.closest("td") : null;
            // 'important' priority: the single-cell-batch selected-cell rules
            // are themselves !important, so a plain inline mute loses.
            gridRoot.querySelectorAll("td.fx-cell-selected").forEach(c => { if (c !== td) muteSelectedLook(c); });
            // Grids that put the row shade on the TDs (fx-cell-row-selected /
            // fx-cell-active on td, not tr) — mute those in the press frame
            // too, or the old row visibly survives a held press.
            gridRoot.querySelectorAll("td.fx-cell-row-selected, td.fx-cell-active").forEach(c => {
                if (c !== td && c.closest("tr") !== tr) muteSelectedLook(c);
            });
            // Row shade as server-written INLINE tr style (gItems model) or
            // as the tr.fx-cell-row-selected class — either way it paints
            // EVERY td of the row directly (!important), so a muted row must
            // mute its cells too.
            gridRoot.querySelectorAll(
                "tbody tr.fx-row[style*='background'], tr.fx-cell-row-selected").forEach(r => {
                if (r === tr) return;
                muteSelectedLook(r);
                for (const c of r.children) { if (c !== td) muteSelectedLook(c); }
            });
            if (td && gridRoot.contains(td)) {
                td.style.setProperty("background-color", gridCellPreviewColor(gridRoot), "important");
                paintedPreviewEls.add(td);
                // Row-shade parity: when the grid highlights the whole selected
                // row (server paints tr.fx-cell-row-selected > td one round trip
                // later), paint this row's OTHER cells in the SAME press frame so
                // the row does not visibly lag the cell. Same colour the server
                // render uses -> flash-free; enrolled so the net/sweep clears them
                // (then the server's fx-cell-row-selected class keeps the row lit).
                if (gridRoot.dataset.fxRowHighlight === "true") {
                    const rowColor = gridCellPreviewColor(gridRoot);
                    for (const c of tr.children) {
                        if (c === td || !c.matches || !c.matches("td")) continue;
                        c.style.setProperty("background-color", rowColor, "important");
                        paintedPreviewEls.add(c);
                    }
                }
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
            // A newer gesture owns the paints now — this net is obsolete.
            if (!isCurrentGridPaintGesture(gridRoot, "pointer", gestureGeneration)) return;
            // A physically live gesture (button still down, or a drag in
            // progress) owns its paints — sweeping now un-mutes the old
            // selection and repaints it under the user's cursor. Defer.
            if (gridPointerGestureLive(gridRoot)) { netTimer = setTimeout(net, 800); return; }
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

// Slide a position:fixed menu back inside the viewport (context menus opened
// at the cursor near the right/bottom edge). Idempotent: only ever moves the
// element up/left just enough to fit, so re-running after the menu grows
// (inline submenu) stays stable.
export function clampMenuIntoViewport(el, margin = 4) {
    if (!el || !el.getBoundingClientRect) return;
    const rect = el.getBoundingClientRect();
    if (!rect.width && !rect.height) return;
    const vw = window.innerWidth, vh = window.innerHeight;
    const left = Math.max(margin, Math.min(rect.left, vw - margin - rect.width));
    const top = Math.max(margin, Math.min(rect.top, vh - margin - rect.height));
    if (left !== rect.left) el.style.left = `${Math.round(left)}px`;
    if (top !== rect.top) el.style.top = `${Math.round(top)}px`;
}


// Server-applied keystrokes during the editor's focus round-trip must reach
// the mounted UNCONTROLLED input too — its mount snapshot predates them, and
// the first native oninput would otherwise overwrite the bridged characters.
export function setBatchEditorValue(input, value) {
    if (!input) return;
    if (input.dataset.fxUserTyped === "1"
        || ("value" in input && input.value !== (input.getAttribute("value") ?? ""))) return;
    input.value = value ?? "";
    try { input.setSelectionRange(input.value.length, input.value.length); } catch { /* non-text input */ }
}

export function getBatchEditorValue(input) {
    return input && typeof input.value === "string" ? input.value : "";
}

export function focusAdjacentOutsideGrid(gridRoot, backwards = false) {
    if (!gridRoot) return false;
    const selector = "a[href], button, input, select, textarea, [tabindex]";
    const candidates = Array.from(document.querySelectorAll(selector)).filter(element => {
        if (element !== gridRoot && gridRoot.contains(element)) return false;
        if (element.disabled || element.tabIndex < 0) return false;
        if (element.closest("[hidden], [aria-hidden='true']")) return false;
        const style = window.getComputedStyle(element);
        return style.display !== "none"
            && style.visibility !== "hidden"
            && element.getClientRects().length > 0;
    });
    const gridIndex = candidates.indexOf(gridRoot);
    if (gridIndex < 0 || candidates.length < 2) return false;

    let targetIndex = backwards ? gridIndex - 1 : gridIndex + 1;
    if (targetIndex < 0) targetIndex = candidates.length - 1;
    if (targetIndex >= candidates.length) targetIndex = 0;
    const target = candidates[targetIndex];
    if (!target || target === gridRoot) return false;
    target.focus({ preventScroll: true });
    return document.activeElement === target;
}

// ── Client-painted keyboard navigation (opt-in) ─────────────────────────────
// Plain arrow presses move the active-cell cue INSTANTLY in the browser and
// never reach Blazor; one debounced sync call lands the real active cell in a
// single server render. Anything the fast path cannot prove safe (modifiers,
// an editor input as target, grouped rows, the row-window edge, no anchor)
// falls through untouched to the server pipeline.
const clientNavBindings = new WeakMap();

export function registerClientNavigationPreview(gridRoot, dotNetRef) {
    if (!gridRoot) return;
    // A circuit reconnect re-registers with a FRESH DotNetObjectReference on
    // the same DOM element; the old listener must adopt it or every arrow is
    // eaten by invokeMethodAsync throwing on the dead circuit's reference.
    const existing = clientNavBindings.get(gridRoot);
    if (existing) { existing.dotNetRef = dotNetRef; return; }
    const binding = { dotNetRef };
    clientNavBindings.set(gridRoot, binding);

    let painted = [];
    let navMuted = new Set();
    let navHiddenBtns = new Set();
    let paintedRowTr = null;
    let pending = null;
    let lastPreview = null;   // survives flush so a held key keeps its anchor
    let stepsSinceFlush = 0;
    let syncTimer = 0;
    let syncInFlight = false;
    let needsFinal = false;   // a previewed position exists that no FINAL sync has landed yet
    let navGeneration = 0;    // this preview's gesture id in the paint arbiter
    let settleGeneration = 0; // non-zero while awaiting the OBSERVED settle render
    let settleArmedPos = null;
    let settleTries = 0;
    let settleTimer = 0;

    // Strip only the cell-cue paints this path wrote. Shared by clearPaints
    // and the keydown fast path, which mid-gesture keeps the row paint and
    // the foreign-selection mutes alive.
    const clearCuePaints = () => {
        for (const el of painted) {
            el.style.removeProperty("background");
            el.style.removeProperty("box-shadow");
        }
        painted = [];
    };

    const clearPaints = () => {
        clearCuePaints();
        if (paintedRowTr) {
            setRowPreview(paintedRowTr, false, "");
            paintedRowTr = null;
        }
        // Unmute ONLY what this path muted — blanket-unmuting would resurrect
        // selection looks the DRAG painter muted mid-gesture.
        for (const el of navMuted) {
            el.style.removeProperty("background-color");
            el.style.removeProperty("box-shadow");
            el.style.removeProperty("outline");
        }
        navMuted.clear();
        for (const b of navHiddenBtns) b.style.removeProperty("display");
        navHiddenBtns.clear();
    };

    const cancelSettle = () => {
        settleGeneration = 0;
        settleArmedPos = null;
        settleTries = 0;
        window.clearTimeout(settleTimer);
        settleTimer = 0;
    };

    // Full preview release: drop every visual and forget the previewed
    // position. The four operations touch disjoint state (settle vars /
    // paint registries / the two flags), so every release site shares this
    // exact sequence.
    const releasePreview = () => {
        cancelSettle();
        clearPaints();
        lastPreview = null;
        needsFinal = false;
    };

    // A pointer gesture is taking over (invoked from beginGridPaintGesture).
    // Land the un-finalized preview position SILENTLY first — same websocket,
    // ordered ahead of the press's own server events — so the press acts on
    // the position the user sees. The pointer gesture owns every visual from
    // here, so no render is requested and all keyboard paints drop now.
    const cancelKeyboardPreviewForPointer = () => {
        const p = pending ?? (needsFinal ? lastPreview : null);
        if (p) {
            try {
                binding.dotNetRef.invokeMethodAsync(
                    "SyncActiveCellFromClientNavigationAsync", p.ari, p.cell, false).catch(() => { });
            } catch { /* circuit down — the press path re-selects anyway */ }
        }
        window.clearTimeout(syncTimer);
        syncTimer = 0;
        pending = null;
        stepsSinceFlush = 0;
        releasePreview();
    };
    gridPaintArbiter(gridRoot).cancelKeyboard = cancelKeyboardPreviewForPointer;

    // Mute every server-rendered selection look on rows other than targetTr.
    // Shared by the keydown fast path (same-frame response) and the render
    // observer below (server renders land AFTER the cursor moved on — a sync
    // checkpoint render repaints a row the preview already left, so keypress-
    // time muting alone leaves stale blue rows between renders). The
    // effective-mute skip makes the sweep idempotent so the observer converges
    // instead of re-triggering itself forever.
    const isEffectivelyMuted = el =>
        el.style.getPropertyValue("background-color") === "transparent";
    // Nav-owned mute: same inline overrides as muteSelectedLook but WITHOUT
    // the data-fx-muted marker / paintedPreviewEls enrollment — the drag
    // path's post-click safety sweep RESTORES marked rows still backed by a
    // selection class, resurrecting exactly what this path just muted.
    const navMuteLook = el => {
        el.style.setProperty("background-color", "transparent", "important");
        el.style.setProperty("box-shadow", "none", "important");
        el.style.setProperty("outline", "none", "important");
        navMuted.add(el);
    };
    const muteForeignSelection = targetTr => {
        gridRoot.querySelectorAll(
            "tbody tr.fx-row.fx-selected, tbody tr.fx-row.fx-cell-row-selected, tbody tr.fx-row[style*=\"background\"], tbody td.fx-cell-row-selected, tbody td.fx-cell-selected, tbody td.fx-cell-active")
            .forEach(el => {
                const tr = el.closest("tr");
                if (tr === targetTr) return;
                if (!isEffectivelyMuted(el)) navMuteLook(el);
                // The row shade is often a SERVER-WRITTEN INLINE STYLE on the
                // tr (gItems paint model) — mute the row AND its cells.
                if (el.tagName === "TR") {
                    for (const td of el.children) {
                        if (!isEffectivelyMuted(td)) navMuteLook(td);
                    }
                }
            });
    };

    // Re-assert the previewed row's paint after a server render: Blazor can
    // replace the tr/td nodes (row windowing) or rewrite their attributes,
    // orphaning the inline paint on detached nodes. Only writes when the
    // current DOM is missing the paint, so the observer converges.
    const ensurePreviewPainted = () => {
        if (!lastPreview) return null;
        const tr = gridRoot.querySelector(`tr.fx-row[data-ari="${lastPreview.ari}"]`);
        if (!tr) return paintedRowTr;
        const trBg = tr.style.backgroundColor;
        if (tr !== paintedRowTr || !trBg || trBg === "transparent") {
            setRowPreview(tr, true, gridPreviewColor(gridRoot));
            paintedRowTr = tr;
        }
        const td = tr.cells[lastPreview.cell];
        if (td) {
            const bs = td.style.getPropertyValue("box-shadow");
            if (!bs || bs === "none") paintCue(td);
        }
        return tr;
    };

    const paintCue = td => {
        // Border only — the cell keeps the row-selection tint while the
        // cursor crosses columns (owner rule: only the border moves).
        td.style.setProperty("box-shadow", "inset 0 0 0 1px var(--fx-grid-editing-cell-border, #6b7f99)", "important");
        painted.push(td);
    };

    const muteCue = (td, keepRowTint = false) => {
        if (!td) return;
        td.style.setProperty(
            "background",
            keepRowTint ? gridPreviewColor(gridRoot) : "transparent",
            "important");
        td.style.setProperty("box-shadow", "none", "important");
        painted.push(td);
    };

    // The server-rendered active-cell position currently in the DOM. The
    // fx-cell-active CLASS is written only by server renders (this path
    // paints cues with inline styles, never classes), so it is an unforgeable
    // "the server's truth reached this DOM" signal.
    const activeCellPos = () => {
        const td = gridRoot.querySelector("td.fx-cell-active");
        if (!td) return null;
        const ari = Number.parseInt(td.closest("tr")?.getAttribute("data-ari") ?? "", 10);
        return Number.isFinite(ari) ? { ari, cell: td.cellIndex } : null;
    };
    const posEq = (a, b) => !!a && !!b && a.ari === b.ari && a.cell === b.cell;

    // Release the preview ONLY once the settle render has been OBSERVED in
    // the DOM — never on a blind timer. A timer release races the render
    // batch on slow links: clearPaints un-mutes the OLD selection an instant
    // before the new one paints (the owner-visible "jumps back" / one-row-
    // behind trail), and every render arriving after the blind clear was
    // unguarded. Release conditions:
    //  - the server's fx-cell-active landed on the previewed cell (agreement:
    //    the settle render is on screen, handoff is seamless), or
    //  - the server's active cell visibly moved somewhere ELSE than where it
    //    was when the settle was armed (an edge-fallthrough key or
    //    programmatic move — server truth supersedes the preview), or
    //  - the retry cap expired (a render that will never come: row scrolled
    //    out of the window, lost circuit) — yield to whatever is on screen.
    const tryReleaseSettled = () => {
        if (!settleGeneration) return;
        if (!lastPreview || !isCurrentGridPaintGesture(gridRoot, "keyboard", settleGeneration)) {
            cancelSettle();
            return;
        }
        if (pending || syncInFlight) return; // gesture resumed — the next final sync re-arms
        const cur = activeCellPos();
        if (posEq(cur, lastPreview)
            || (cur && settleArmedPos && !posEq(cur, settleArmedPos))
            || settleTries >= 10) {
            releasePreview();
            return;
        }
        settleTries++;
        window.clearTimeout(settleTimer);
        settleTimer = window.setTimeout(tryReleaseSettled, 300);
    };

    const armSettleRelease = generation => {
        settleGeneration = generation;
        settleTries = 0;
        settleArmedPos = activeCellPos();
        // The settle render may already be in the DOM (fast link) — release
        // immediately; otherwise the render observer or the guarded retry
        // timer performs the release the moment the render arrives.
        tryReleaseSettled();
    };

    // final=false → mid-hold state catch-up: the server adopts the position
    // WITHOUT rendering, so nothing repaints behind the flying cursor (the
    // paint war between checkpoint renders and the preview was the visible
    // "jumps back" during a held key). final=true → the settle sync: one
    // render paints the authoritative selection, and the paints are released
    // only when that render is OBSERVED (tryReleaseSettled).
    const flush = final => {
        window.clearTimeout(syncTimer);
        syncTimer = 0;
        // A final flush with nothing pending still re-sends the last preview
        // when no final sync has landed it yet (needsFinal): a checkpoint
        // flush consumes `pending`, and without this the gesture could end
        // silently adopted but never rendered — paints parked forever.
        const p = pending ?? ((final && needsFinal) ? lastPreview : null);
        if (!p) return;
        // One sync outstanding at a time: queued syncs drain as a visible
        // selection replay after the key is released.
        if (syncInFlight) {
            syncTimer = window.setTimeout(() => { syncTimer = 0; flush(final); }, 80);
            return;
        }
        stepsSinceFlush = 0;
        const generation = navGeneration;
        pending = null;
        syncInFlight = true;
        try {
            binding.dotNetRef.invokeMethodAsync("SyncActiveCellFromClientNavigationAsync", p.ari, p.cell, !!final)
                .then(adopted => {
                    syncInFlight = false;
                    // A newer gesture owns the visuals — its own syncs manage them.
                    if (!isCurrentGridPaintGesture(gridRoot, "keyboard", generation)) return;
                    if (pending) { flush(final); return; }
                    if (final) {
                        needsFinal = false;
                        if (adopted === false) {
                            // The server refused the position (guard/veto):
                            // no settle render is coming — yield to server
                            // truth right away.
                            releasePreview();
                            return;
                        }
                        armSettleRelease(generation);
                        return;
                    }
                    // Backstop: the keydown path re-arms the trailing final
                    // flush after every checkpoint, but if none is armed when
                    // this checkpoint lands, arm one — an un-finalized preview
                    // must always end in a final sync that settles.
                    if (needsFinal && !syncTimer)
                        syncTimer = window.setTimeout(() => { syncTimer = 0; flush(true); }, 140);
                })
                // lastPreview must go too or the render observer repaints
                // what clearPaints just removed.
                .catch(() => {
                    syncInFlight = false;
                    if (!isCurrentGridPaintGesture(gridRoot, "keyboard", generation)) return;
                    releasePreview();
                });
        } catch {
            syncInFlight = false;
            releasePreview();
        }
    };

    // Land the outstanding previewed position with a FINAL sync before a key
    // or press falls through to the server pipeline.
    const flushPreviewPosition = () => { if (pending || needsFinal) flush(true); };

    const onKeyDown = event => {
        // NOTE: the grid's own keyboard trap preventDefaults trusted arrows at
        // document capture (page-scroll suppression) BEFORE this listener —
        // defaultPrevented is NOT a foreign claim here.
        const key = event.key;
        const isArrow = key === "ArrowDown" || key === "ArrowUp" || key === "ArrowLeft" || key === "ArrowRight";
        const t = event.target;
        const inEditor = t instanceof Element && t.matches("input, select, textarea, [contenteditable='true']");
        // An in-cell popup that owns its own arrows opts out of the fast path,
        // which is capture-phase and would otherwise drive the grid cursor
        // underneath it.
        const inKeyScope = t instanceof Element && !!t.closest("[data-fx-key-scope]");
        // An OPEN batch editor/dropdown handles arrows SERVER-side while focus
        // stays on the grid host — the fast path must yield or it steals the
        // dropdown's arrow keys and navigates the grid underneath it.
        const editorOpen = !!gridRoot.querySelector("td.fx-batch-editing, td.fx-batch-dropdown-editing");
        if (!isArrow || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || inEditor || editorOpen || inKeyScope) {
            // The next key runs server-side: land the previewed position first
            // (same websocket, ordered) so it acts on the right cell. Also
            // when only needsFinal is outstanding — the final sync renders,
            // the settle handshake releases, and the server key's own render
            // then owns the screen (the old pending-only check left the
            // preview fighting that render).
            flushPreviewPosition();
            return;
        }

        const anchor = pending ?? lastPreview;
        let cur = anchor
            ? gridRoot.querySelector(`tr.fx-row[data-ari="${anchor.ari}"]`)?.cells[anchor.cell]
            : null;
        if (!cur) cur = gridRoot.querySelector("td.fx-cell-active");
        if (!cur) { flushPreviewPosition(); return; }

        const tr = cur.closest("tr");
        const ari = Number.parseInt(tr?.getAttribute("data-ari") ?? "", 10);
        if (!Number.isFinite(ari)) return;

        let target = null;
        if (key === "ArrowDown" || key === "ArrowUp") {
            const nextTr = gridRoot.querySelector(
                `tr.fx-row[data-ari="${ari + (key === "ArrowDown" ? 1 : -1)}"]`);
            if (!nextTr) { flushPreviewPosition(); return; } // window edge — sync first, server shifts the window
            target = nextTr.cells[cur.cellIndex];
        } else {
            let td = cur;
            do {
                td = key === "ArrowRight" ? td.nextElementSibling : td.previousElementSibling;
            } while (td && td.tagName !== "TD");
            if (!td) { flushPreviewPosition(); return; } // row edge — sync first, server owns wrap rules
            target = td;
        }
        if (!target) return;


        // Enroll this preview with the paint arbiter: pointer-era safety nets
        // become obsolete (their generation is stale) and a pointer gesture
        // starting later cancels this preview through cancelKeyboard.
        if (!isCurrentGridPaintGesture(gridRoot, "keyboard", navGeneration))
            navGeneration = beginGridPaintGesture(gridRoot, "keyboard");

        event.preventDefault();
        event.stopImmediatePropagation();

        // Selection follows the active cell: move the ROW look client-side too,
        // or it trails at the sync cadence and visibly lags a held key.
        const newTr = target.closest("tr");
        clearCuePaints();
        if (paintedRowTr && paintedRowTr !== newTr) setRowPreview(paintedRowTr, false, "");
        // Mute the old selection look in the same frame — both row-mode
        // (tr.fx-selected) and cell-mode (row shade on TD classes) variants —
        // tracking every muted element locally.
        muteForeignSelection(newTr);
        setRowPreview(newTr, true, gridPreviewColor(gridRoot));
        paintedRowTr = newTr;
        const oldActive = gridRoot.querySelector("td.fx-cell-active");
        muteCue(oldActive, oldActive?.closest("tr") === newTr);
        // The old cell's server-rendered adornments (the "..." popup button)
        // must leave in the SAME frame as the cursor, not at the sync render.
        if (oldActive && oldActive !== target) {
            oldActive.querySelectorAll(
                ".fx-cell-edit-btn, .fx-cell-action-btn, .fx-grid-popup-btn, [data-fx-cell-popup]")
                .forEach(b => { b.style.setProperty("display", "none", "important"); navHiddenBtns.add(b); });
        }
        paintCue(target);
        target.scrollIntoView({ block: "nearest", inline: "nearest" });

        pending = {
            ari: Number.parseInt(newTr.getAttribute("data-ari"), 10),
            cell: target.cellIndex
        };
        lastPreview = pending;
        needsFinal = true;
        cancelSettle(); // the gesture resumed — a pending settle-await is void
        // A held key resets the trailing debounce forever — flush a silent
        // checkpoint every 20 steps so the server keeps pace with a long
        // hold, and ALWAYS re-arm the trailing final flush afterwards: the
        // old early-return left a hold released exactly on a checkpoint step
        // with no trailing timer — no settle render, paints parked forever.
        if (++stepsSinceFlush >= 20) flush(false);
        window.clearTimeout(syncTimer);
        syncTimer = window.setTimeout(() => { syncTimer = 0; flush(true); }, 140);
    };

    gridRoot.addEventListener("keydown", onKeyDown, true);

    // Re-apply the preview paint + foreign-selection mutes after EVERY server
    // render while a preview is live. Sync checkpoint renders land behind the
    // cursor and repaint rows the preview already left (server classes on new
    // nodes the keypress-time mutes never saw); on slow links those straggle
    // long enough to show 2-3 highlighted rows at once. The observer runs as a
    // microtask BEFORE the browser paints the render (same trick as
    // registerActiveCellScrollSync), so the stale paint never reaches the
    // screen. Inert whenever no preview is live (lastPreview null); both
    // helpers only write when the DOM disagrees, so the observer's own
    // mutations converge instead of looping.
    const renderObserver = new MutationObserver(() => {
        if (!lastPreview || !isCurrentGridPaintGesture(gridRoot, "keyboard", navGeneration)) return;
        const cur = activeCellPos();
        if (cur && !posEq(cur, lastPreview)
            && !pending && !syncInFlight && !syncTimer && !settleGeneration) {
            // The server moved the active cell on its own — no sync of ours
            // is outstanding, so this is not checkpoint lag (an edge-
            // fallthrough key or a programmatic move). Its truth supersedes
            // the preview: release instead of fighting the render.
            releasePreview();
            return;
        }
        muteForeignSelection(ensurePreviewPainted());
        // While a settle-await is armed, this render may BE the settle
        // render — release in the same pre-paint microtask (seamless swap
        // from inline preview to the server's identical selection look).
        tryReleaseSettled();
    });
    renderObserver.observe(gridRoot, {
        subtree: true,
        childList: true,
        attributes: true,
        attributeFilter: ["class", "style"]
    });

    // Fallback take-over for presses the instant-feedback binding does not
    // enroll with the arbiter (non-primary buttons, modifier presses, presses
    // on editor controls or outside data rows, grids with no instant-feedback
    // binding): land the previewed position first, then drop this path's
    // paints in the same frame, or the drag painter (which cannot see these
    // inline paints) leaves the previewed row tinted until mouse-up. Plain
    // primary-button row presses never reach past the owner check — the
    // arbiter already ran cancelKeyboardPreviewForPointer from the pointerdown.
    gridRoot.addEventListener("mousedown", () => {
        if (gridPaintArbiter(gridRoot).owner === "pointer") return;
        flushPreviewPosition();
        releasePreview();
    }, true);
}
