/* FlexKit — browser-side helpers for GridControl.
 *
 * Loaded on demand by GridControl via Blazor's dynamic-import interop:
 *   _gridJsModule = await JsRuntime.InvokeAsync<IJSObjectReference>(
 *       "import", GridJsModulePath);
 *
 * One function per concern, exported as ES module bindings so the host
 * page doesn't get a global namespace dumped into it. Keep this file tiny
 * — Grid markup-side concerns go in GridControl.razor.css, not here.
 */

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

        const contentEl = getGridBodyViewportElement(gridRoot);
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

    const contentEl = getGridContentElement(gridRoot);
    const bodyViewportEl = getGridBodyViewportElement(gridRoot);
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
