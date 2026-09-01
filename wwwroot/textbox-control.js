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

const replaceOnFirstInputState = new WeakMap();
const clientBufferedTypingBindings = new WeakMap();

// ── Pre-attach typing shield ────────────────────────────────────────────────
// enableClientBufferedTyping arrives one interop round-trip AFTER a
// ClientBuffered editor renders. Keys typed in that gap used to dispatch to
// Blazor, and the renders those dispatches trigger re-apply the stale rendered
// value over the DOM — eating or resurrecting the first characters at WAN
// latency. Inputs that render data-fx-cb-pending="1" are shielded from the
// moment they exist: this document-level CAPTURE listener stops typing-key
// keydowns from ever reaching Blazor's delegated handlers (the browser still
// performs the native insertion, and the input/change events still flow so the
// server's edit buffer stays in sync). Once the real buffer attaches
// (data-fx-client-buffered-editor="1"), the per-element handlers take over and
// this shield stands down.
document.addEventListener("keydown", event => {
    const t = event.target;
    if (!t || !t.dataset
        || t.dataset.fxCbPending !== "1"
        || t.dataset.fxClientBufferedEditor === "1")
        return;
    if (event.altKey || event.ctrlKey || event.metaKey || event.isComposing)
        return;
    const key = event.key;
    if (key.length === 1
        || key === "Backspace"
        || key === "Delete"
        || key === "ArrowLeft"
        || key === "ArrowRight"
        || key === "Home"
        || key === "End") {
        event.stopPropagation();
        t.dataset.fxUserTyped = "1";
    }
}, true);

export function enableReplaceOnFirstInput(element) {
    const state = ensureReplaceOnFirstInputState(element);
    if (!state || state.enabled) return;

    state.enabled = true;
    element.addEventListener("focus", () => {
        state.armed = true;
    });
}

export function armReplaceOnFirstInput(element) {
    const state = ensureReplaceOnFirstInputState(element);
    if (state) {
        state.armed = true;
    }
}

function ensureReplaceOnFirstInputState(element) {
    if (!element) return null;

    let state = replaceOnFirstInputState.get(element);
    if (!state) {
        state = { armed: false, enabled: false };
        replaceOnFirstInputState.set(element, state);

        element.addEventListener("pointerdown", () => {
            if (document.activeElement === element) {
                state.armed = false;
            }
        }, true);

        element.addEventListener("beforeinput", event => {
            if (!state.armed) return;

            const inputType = event.inputType || "";
            if (inputType === "insertText" || inputType === "insertCompositionText") {
                event.preventDefault();
                state.armed = false;
                replaceWholeText(element, event.data || "");
                notifyTextChanged(element);
                return;
            }

            if (inputType === "deleteContentBackward" || inputType === "deleteContentForward") {
                event.preventDefault();
                state.armed = false;
                replaceWholeText(element, "");
                notifyTextChanged(element);
            }
        });

        element.addEventListener("input", () => {
            state.armed = false;
        });
    }

    return state;
}

export function getTextContextMenuState(element) {
    const value = getTextValue(element);
    const selection = getTextSelection(element);
    return {
        HasSelection: selection.end > selection.start,
        HasValue: value.length > 0,
        AllSelected: value.length > 0 && selection.start === 0 && selection.end === value.length,
        IsRightToLeft: isRightToLeft(element)
    };
}

export function positionTextContextMenu(clientX, clientY, width = 316, height = 342) {
    const viewportWidth = window.innerWidth || document.documentElement.clientWidth || width;
    const viewportHeight = window.innerHeight || document.documentElement.clientHeight || height;
    const maxX = Math.max(4, viewportWidth - width - 4);
    const maxY = Math.max(4, viewportHeight - height - 4);

    return {
        X: Math.min(Math.max(4, clientX), maxX),
        Y: Math.min(Math.max(4, clientY), maxY)
    };
}

export async function applyTextContextCommand(element, command) {
    if (!element) {
        return { Value: "", ValueChanged: false };
    }

    const before = getTextValue(element);
    try {
        element.focus({ preventScroll: true });
    } catch {
        try { element.focus(); } catch { }
    }

    switch (command) {
        case "undo":
            execTextCommand("undo");
            break;
        case "cut":
            await cutSelection(element);
            break;
        case "copy":
            await copySelection(element);
            break;
        case "paste":
            await pasteClipboard(element);
            break;
        case "delete":
            deleteSelection(element);
            break;
        case "selectAll":
            selectAllText(element);
            break;
        case "readingOrder":
            toggleReadingOrder(element);
            break;
        case "ime":
            break;
    }

    const after = getTextValue(element);
    return {
        Value: after,
        ValueChanged: before !== after
    };
}

export function getTextValue(element) {
    return typeof element.value === "string" ? element.value : "";
}

function getTextSelection(element) {
    try {
        const valueLength = getTextValue(element).length;
        const start = typeof element.selectionStart === "number" ? element.selectionStart : valueLength;
        const end = typeof element.selectionEnd === "number" ? element.selectionEnd : start;
        return {
            start: Math.max(0, Math.min(start, valueLength)),
            end: Math.max(0, Math.min(end, valueLength))
        };
    } catch {
        const valueLength = getTextValue(element).length;
        return { start: valueLength, end: valueLength };
    }
}

function getSelectedText(element) {
    const value = getTextValue(element);
    const selection = getTextSelection(element);
    return value.substring(selection.start, selection.end);
}

function selectAllText(element) {
    if (typeof element.select === "function") {
        element.select();
        return;
    }

    setTextSelection(element, 0, getTextValue(element).length);
}

function setTextSelection(element, start, end = start) {
    try {
        if (typeof element.setSelectionRange === "function") {
            element.setSelectionRange(start, end);
        }
    } catch { }
}

function replaceSelection(element, text) {
    const value = getTextValue(element);
    const selection = getTextSelection(element);
    const before = value.substring(0, selection.start);
    const after = value.substring(selection.end);
    const maxLength = typeof element.maxLength === "number" ? element.maxLength : -1;
    const allowedInsertLength = maxLength >= 0
        ? Math.max(0, maxLength - before.length - after.length)
        : text.length;
    const insertText = text.substring(0, allowedInsertLength);
    const next = before + insertText + after;
    const caret = before.length + insertText.length;
    element.value = next;
    setTextSelection(element, caret);
}

function replaceWholeText(element, text) {
    const maxLength = typeof element.maxLength === "number" ? element.maxLength : -1;
    const next = maxLength >= 0 ? text.substring(0, maxLength) : text;
    element.value = next;
    setTextSelection(element, next.length);
}

function notifyTextChanged(element) {
    element.dispatchEvent(new Event("input", { bubbles: true }));
}

function deleteSelection(element) {
    if (!getTextContextMenuState(element).HasSelection) return;
    if (!execTextCommand("delete")) {
        replaceSelection(element, "");
    }
}

async function cutSelection(element) {
    const selectedText = getSelectedText(element);
    if (!selectedText) return;

    if (execTextCommand("cut")) {
        return;
    }

    await writeClipboardText(selectedText);
    replaceSelection(element, "");
}

async function copySelection(element) {
    const selectedText = getSelectedText(element);
    if (!selectedText) return;

    if (execTextCommand("copy")) {
        return;
    }

    await writeClipboardText(selectedText);
}

async function pasteClipboard(element) {
    let pastedText = "";
    try {
        if (navigator.clipboard && typeof navigator.clipboard.readText === "function") {
            pastedText = await navigator.clipboard.readText();
        }
    } catch {
        pastedText = "";
    }

    if (pastedText) {
        replaceSelection(element, pastedText);
        return;
    }

    execTextCommand("paste");
}

async function writeClipboardText(text) {
    try {
        if (navigator.clipboard && typeof navigator.clipboard.writeText === "function") {
            await navigator.clipboard.writeText(text);
        }
    } catch { }
}

function execTextCommand(command) {
    try {
        if (typeof document.execCommand === "function") {
            return document.execCommand(command);
        }
    } catch { }
    return false;
}

function isRightToLeft(element) {
    const dir = (element.getAttribute("dir") || element.dir || "").toLowerCase();
    if (dir === "rtl") return true;
    if (dir === "ltr") return false;
    try {
        return window.getComputedStyle(element).direction === "rtl";
    } catch {
        return false;
    }
}

function toggleReadingOrder(element) {
    element.dir = isRightToLeft(element) ? "ltr" : "rtl";
}


// Multi-line editors that opt in (TabSpaces > 0): Tab inserts spaces at the
// caret instead of moving focus out of the app.
export function registerTabCapture(el, spaces) {
    if (!el) return;
    el.dataset.fxTabCapture = "1"; // tells the dialog tab loop this editor owns Tab
    el.addEventListener("keydown", e => {
        if (e.key !== "Tab" || e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) return;
        e.preventDefault();
        el.setRangeText(" ".repeat(spaces), el.selectionStart, el.selectionEnd, "end");
        el.dispatchEvent(new Event("input", { bubbles: true }));
    });
}

// Typing keys never need a server dispatch: the input is uncontrolled (the DOM
// owns the text) and the host's keydown handler ignores them anyway, so each
// character was paying a SignalR round trip for nothing — that is the "every
// character struggles / pulls back" feel, and it made fast Backspace crawl.
// Blazor listens for keydown at the document root, so stopping propagation on
// the element itself means the event is never dispatched to .NET. Keys the grid
// genuinely acts on (Tab/Enter/Escape/arrows/paging/F-keys, or anything with a
// modifier) are passed straight through, untouched.
export function suppressTypingKeyDispatch(el) {
    if (!el || el.dataset.fxTypingKeysLocal === "1") return;
    el.dataset.fxTypingKeysLocal = "1";
    el.addEventListener("keydown", e => {
        if (e.altKey || e.ctrlKey || e.metaKey) return;
        const ownedByEditor = e.key.length === 1 || e.key === "Backspace" || e.key === "Delete";
        if (ownedByEditor) e.stopPropagation();
    });
}

// Opt-in Blazor Server fast path. The browser owns ordinary text mutations and
// sends one completed value to TextBoxControl on navigation, Enter, or blur.
export function enableClientBufferedTyping(el, dotNetRef, handlesNavigationKeys) {
    if (!el || !dotNetRef) return;

    let binding = clientBufferedTypingBindings.get(el);
    if (binding) {
        binding.dotNetRef = dotNetRef;
        binding.handlesNavigationKeys = !!handlesNavigationKeys;
        return;
    }

    binding = {
        dotNetRef,
        handlesNavigationKeys: !!handlesNavigationKeys,
        commitPending: false,
        composing: false,
        cleanup: null
    };

    const commit = (key, event) => {
        if (binding.commitPending) return;
        binding.commitPending = true;

        try {
            const invocation = binding.dotNetRef.invokeMethodAsync(
                "CommitClientBufferedTypingAsync",
                el.value ?? "",
                key,
                !!event?.shiftKey,
                !!event?.ctrlKey,
                !!event?.altKey,
                !!event?.metaKey);

            Promise.resolve(invocation)
                .catch(() => { })
                .finally(() => {
                    if (el.isConnected)
                        binding.commitPending = false;
                });
        } catch {
            binding.commitPending = false;
        }
    };

    const onKeyDown = event => {
        if (!binding.handlesNavigationKeys || binding.composing || event.isComposing)
            return;

        const key = event.key;
        const controlBoundaryKey = (event.ctrlKey || event.metaKey)
            && (key === "Home" || key === "End");

        // Preserve application shortcuts such as Ctrl+S; only Ctrl/Cmd+Home/End
        // belongs to the grid's navigation contract.
        if ((event.altKey || event.ctrlKey || event.metaKey) && !controlBoundaryKey)
            return;

        const hasCollapsedCaret = typeof el.selectionStart === "number"
            && typeof el.selectionEnd === "number"
            && el.selectionStart === el.selectionEnd;
        const horizontalCaretBoundaryKey = !event.shiftKey
            && hasCollapsedCaret
            && ((key === "ArrowLeft" && el.selectionStart <= 0)
                || (key === "ArrowRight" && el.selectionEnd >= (el.value?.length ?? 0)));

        let commits = key === "Enter"
            || key === "NumpadEnter"
            || key === "Escape"
            || key === "Tab"
            || key === "ArrowUp"
            || key === "ArrowDown"
            || key === "PageUp"
            || key === "PageDown"
            || controlBoundaryKey
            || horizontalCaretBoundaryKey;

        const staysInEditor = key.length === 1
            || key === "Backspace"
            || key === "Delete"
            || key === "ArrowLeft"
            || key === "ArrowRight"
            || key === "Home"
            || key === "End";

        if (!commits) {
            if (staysInEditor)
                event.stopPropagation();
            return;
        }

        event.stopPropagation();
        event.preventDefault();
        commit(key, event);
    };

    const stopServerDispatch = event => {
        event.stopPropagation();
        // Mark that a REAL user edit happened in this element. The grid's
        // pending-editor relay (setBatchEditorValue) and the deferred
        // select-all/caret placement consult this flag: once the user has
        // typed here, any late server push or selection is stale by
        // definition and must not clobber the DOM text.
        if (event.type === "input")
            el.dataset.fxUserTyped = "1";
    };
    const onBlur = event => {
        event.stopPropagation();
        commit("Blur", event);
    };
    const onCompositionStart = () => { binding.composing = true; };
    const onCompositionEnd = () => { binding.composing = false; };

    el.addEventListener("keydown", onKeyDown);
    el.addEventListener("input", stopServerDispatch);
    el.addEventListener("change", stopServerDispatch);
    el.addEventListener("blur", onBlur);
    el.addEventListener("compositionstart", onCompositionStart);
    el.addEventListener("compositionend", onCompositionEnd);

    binding.cleanup = () => {
        el.removeEventListener("keydown", onKeyDown);
        el.removeEventListener("input", stopServerDispatch);
        el.removeEventListener("change", stopServerDispatch);
        el.removeEventListener("blur", onBlur);
        el.removeEventListener("compositionstart", onCompositionStart);
        el.removeEventListener("compositionend", onCompositionEnd);
    };

    clientBufferedTypingBindings.set(el, binding);
    el.dataset.fxClientBufferedEditor = "1";
}

export function disableClientBufferedTyping(el) {
    if (!el) return;
    const binding = clientBufferedTypingBindings.get(el);
    if (binding?.cleanup)
        binding.cleanup();
    clientBufferedTypingBindings.delete(el);
    delete el.dataset.fxClientBufferedEditor;
    delete el.dataset.fxUserTyped;
}


// A native paste is clipped by the maxlength attribute BEFORE any input event
// fires, so the server can never learn that characters were dropped. This
// listener measures the clipboard against the room left and reports the loss,
// which is what lets a host say "pasted value was shortened" instead of the
// user discovering it later (or hitting a database truncation error).
export function enableMaxLengthPasteNotice(el, dotNetRef, maxLength) {
    if (!el || el.dataset.fxPasteNotice === "1" || !(maxLength > 0)) return;
    el.dataset.fxPasteNotice = "1";
    el.addEventListener("paste", e => {
        try {
            const pasted = (e.clipboardData || window.clipboardData)?.getData("text") ?? "";
            if (!pasted) return;
            const selected = Math.abs((el.selectionEnd ?? 0) - (el.selectionStart ?? 0));
            const room = maxLength - (el.value.length - selected);
            if (pasted.length > room) {
                dotNetRef.invokeMethodAsync("OnPasteTruncatedAsync", pasted.length, maxLength);
            }
        } catch { /* clipboard unavailable — the clamp still applies */ }
    });
}

// ── Classic VB6-Style Vertical Scrollbar for TextAreaControl ────────────────
const textAreaScrollBarBindings = new WeakMap();

export function registerTextAreaScrollBar(hostEl, textareaEl, scrollbarEl) {
    if (!hostEl || !textareaEl || !scrollbarEl) return;

    unregisterTextAreaScrollBar(textareaEl);

    const upBtn = scrollbarEl.querySelector(".fx-scrollbar-arrow-up");
    const downBtn = scrollbarEl.querySelector(".fx-scrollbar-arrow-down");
    const trackEl = scrollbarEl.querySelector(".fx-scrollbar-track");
    const thumbEl = scrollbarEl.querySelector(".fx-scrollbar-thumb");

    if (!upBtn || !downBtn || !trackEl || !thumbEl) return;

    let isDragging = false;
    let dragStartY = 0;
    let dragStartScrollTop = 0;
    let isScrollActive = false;

    function updateScrollBar() {
        const scrollHeight = textareaEl.scrollHeight;
        const clientHeight = textareaEl.clientHeight;
        const scrollTop = textareaEl.scrollTop;
        const canScroll = (scrollHeight - clientHeight) > 1.5;

        if (canScroll !== isScrollActive) {
            isScrollActive = canScroll;
            if (canScroll) {
                scrollbarEl.classList.remove("is-disabled");
                scrollbarEl.classList.add("is-enabled");
                upBtn.removeAttribute("disabled");
                downBtn.removeAttribute("disabled");
                thumbEl.style.display = "block";
            } else {
                scrollbarEl.classList.remove("is-enabled");
                scrollbarEl.classList.add("is-disabled");
                upBtn.setAttribute("disabled", "disabled");
                downBtn.setAttribute("disabled", "disabled");
                thumbEl.style.display = "none";
            }
        }

        if (canScroll) {
            const trackHeight = trackEl.clientHeight;
            if (trackHeight > 0) {
                const thumbHeight = Math.max(16, Math.round((clientHeight / scrollHeight) * trackHeight));
                const maxThumbTop = trackHeight - thumbHeight;
                const maxScroll = scrollHeight - clientHeight;
                const thumbTop = maxScroll > 0 ? Math.round((scrollTop / maxScroll) * maxThumbTop) : 0;

                thumbEl.style.height = `${thumbHeight}px`;
                thumbEl.style.top = `${thumbTop}px`;
            }
        }
    }

    const onScroll = () => {
        if (!isDragging) {
            updateScrollBar();
        }
    };

    const onInput = () => {
        requestAnimationFrame(updateScrollBar);
    };

    const onKeyDown = () => {
        requestAnimationFrame(updateScrollBar);
    };

    const onUpClick = (e) => {
        e.preventDefault();
        e.stopPropagation();
        if (!isScrollActive) return;
        textareaEl.scrollTop -= 18;
        updateScrollBar();
    };

    const onDownClick = (e) => {
        e.preventDefault();
        e.stopPropagation();
        if (!isScrollActive) return;
        textareaEl.scrollTop += 18;
        updateScrollBar();
    };

    const onTrackMouseDown = (e) => {
        if (e.target === thumbEl || !isScrollActive) return;
        e.preventDefault();
        const rect = trackEl.getBoundingClientRect();
        const clickY = e.clientY - rect.top;
        const thumbRect = thumbEl.getBoundingClientRect();
        const thumbY = thumbRect.top - rect.top;

        if (clickY < thumbY) {
            textareaEl.scrollTop -= textareaEl.clientHeight * 0.8;
        } else {
            textareaEl.scrollTop += textareaEl.clientHeight * 0.8;
        }
        updateScrollBar();
    };

    const onThumbPointerDown = (e) => {
        if (!isScrollActive) return;
        e.preventDefault();
        e.stopPropagation();
        isDragging = true;
        dragStartY = e.clientY;
        dragStartScrollTop = textareaEl.scrollTop;
        thumbEl.classList.add("is-active");
        try { thumbEl.setPointerCapture(e.pointerId); } catch { }

        const onPointerMove = (ev) => {
            if (!isDragging) return;
            ev.preventDefault();
            const deltaY = ev.clientY - dragStartY;
            const trackHeight = trackEl.clientHeight;
            const thumbHeight = thumbEl.offsetHeight;
            const maxThumbTop = trackHeight - thumbHeight;
            const maxScroll = textareaEl.scrollHeight - textareaEl.clientHeight;

            if (maxThumbTop > 0 && maxScroll > 0) {
                const scrollDelta = (deltaY / maxThumbTop) * maxScroll;
                textareaEl.scrollTop = dragStartScrollTop + scrollDelta;
                updateScrollBar();
            }
        };

        const onPointerUp = (ev) => {
            isDragging = false;
            thumbEl.classList.remove("is-active");
            try { thumbEl.releasePointerCapture(ev.pointerId); } catch { }
            thumbEl.removeEventListener("pointermove", onPointerMove);
            thumbEl.removeEventListener("pointerup", onPointerUp);
            thumbEl.removeEventListener("pointercancel", onPointerUp);
            updateScrollBar();
        };

        thumbEl.addEventListener("pointermove", onPointerMove);
        thumbEl.addEventListener("pointerup", onPointerUp);
        thumbEl.addEventListener("pointercancel", onPointerUp);
    };

    textareaEl.addEventListener("scroll", onScroll, { passive: true });
    textareaEl.addEventListener("input", onInput, { passive: true });
    textareaEl.addEventListener("keyup", onKeyDown, { passive: true });
    textareaEl.addEventListener("keydown", onKeyDown, { passive: true });
    upBtn.addEventListener("click", onUpClick);
    downBtn.addEventListener("click", onDownClick);
    trackEl.addEventListener("mousedown", onTrackMouseDown);
    thumbEl.addEventListener("pointerdown", onThumbPointerDown);

    let resizeObserver = null;
    if (typeof ResizeObserver !== "undefined") {
        resizeObserver = new ResizeObserver(() => {
            requestAnimationFrame(updateScrollBar);
        });
        resizeObserver.observe(textareaEl);
        resizeObserver.observe(hostEl);
    }

    // Initial update
    requestAnimationFrame(updateScrollBar);
    setTimeout(updateScrollBar, 50);

    const binding = {
        cleanup: () => {
            textareaEl.removeEventListener("scroll", onScroll);
            textareaEl.removeEventListener("input", onInput);
            textareaEl.removeEventListener("keyup", onKeyDown);
            textareaEl.removeEventListener("keydown", onKeyDown);
            upBtn.removeEventListener("click", onUpClick);
            downBtn.removeEventListener("click", onDownClick);
            trackEl.removeEventListener("mousedown", onTrackMouseDown);
            thumbEl.removeEventListener("pointerdown", onThumbPointerDown);
            if (resizeObserver) {
                resizeObserver.disconnect();
            }
        },
        update: updateScrollBar
    };

    textAreaScrollBarBindings.set(textareaEl, binding);
}

export function unregisterTextAreaScrollBar(textareaEl) {
    if (!textareaEl) return;
    const binding = textAreaScrollBarBindings.get(textareaEl);
    if (binding?.cleanup) {
        binding.cleanup();
    }
    textAreaScrollBarBindings.delete(textareaEl);
}

export function updateTextAreaScrollBar(textareaEl) {
    if (!textareaEl) return;
    const binding = textAreaScrollBarBindings.get(textareaEl);
    if (binding?.update) {
        binding.update();
    }
}
