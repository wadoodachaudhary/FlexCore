/* FlexKit — browser-side helpers for GridControl.
 *
 * Loaded on demand by GridControl via Blazor's dynamic-import interop:
 *   _gridJsModule = await JsRuntime.InvokeAsync<IJSObjectReference>(
 *       "import", "./_content/FlexKit/grid-control.js");
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
    return gridRoot.querySelector(".fx-grid-content, .hf-grid-content");
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
    const row = target?.closest?.(".fx-grid-body .fx-row, .hf-grid-body .hf-row, .fx-row, .hf-row");
    return row && contentEl.contains(row) ? row : null;
}

function notifyRowDragSelection(row, state, contentEl) {
    if (!row || (state.lastRow === row && row.isConnected)) return;

    state.lastRow = row;
    const rows = Array.from(contentEl.querySelectorAll(".fx-grid-body .fx-row, .hf-grid-body .hf-row"));
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

        const contentEl = getGridContentElement(gridRoot);
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

        const header = target.closest(".fx-header-cell, .hf-header-cell");
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

        const header = target.closest(".fx-header-cell, .hf-header-cell");
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

        const row = target.closest(".fx-grid-body .fx-row, .hf-grid-body .hf-row, .fx-row, .hf-row");
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
