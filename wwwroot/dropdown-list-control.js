export function measureDropdown(host, desiredMaxHeight = 180, margin = 8, panelWidthOverride = 0, panelElement = null) {
    if (!host) {
        return { openUp: false, maxHeight: desiredMaxHeight, top: margin, left: margin, minWidth: 0 };
    }

    const rect = host.getBoundingClientRect();
    // Callers whose popup is not a .fx-dropdown-panel (DropDownGridControl) pass the
    // popup element so its REAL rendered height drives the flip and the placement.
    const panel = panelElement || host.querySelector(".fx-dropdown-panel");
    const viewportWidth = window.innerWidth || document.documentElement.clientWidth || 0;
    const viewportHeight = window.innerHeight || document.documentElement.clientHeight || 0;

    // HHM-576: the panel is position:absolute inside the host, so any scrollable
    // ancestor (computed overflow-y auto/scroll/hidden/clip/overlay) clips it.
    // Measuring the open-up flip against the viewport alone let the panel open
    // downward into a scroll container's overflow — the options merely extended
    // the container's scrollHeight and stayed hidden until the user scrolled.
    // Walk the clip ancestors and intersect their rects with the viewport, then
    // measure the space above/below against that visible rect so the panel flips
    // upward when the host sits at the bottom of a scrollable region (matching
    // the VB6 combo auto-flip behavior).
    let visibleTop = 0;
    let visibleBottom = viewportHeight;
    let ancestor = host.parentElement;
    while (ancestor && ancestor !== document.body && ancestor !== document.documentElement) {
        const overflowY = (window.getComputedStyle(ancestor).overflowY || "").toLowerCase();
        if (overflowY && overflowY !== "visible") {
            const ancestorRect = ancestor.getBoundingClientRect();
            visibleTop = Math.max(visibleTop, ancestorRect.top);
            visibleBottom = Math.min(visibleBottom, ancestorRect.bottom);
        }
        ancestor = ancestor.parentElement;
    }

    // Border-box math: max-height applies to the border box, but scrollHeight is
    // content-box. The old code set max-height = scrollHeight, leaving the panel
    // exactly its borders (2px) short of its own content — a permanent scrollbar
    // with the last option clipped on every list taller than one row.
    const cs = panel ? window.getComputedStyle(panel) : null;
    const borderY = cs ? (parseFloat(cs.borderTopWidth) || 0) + (parseFloat(cs.borderBottomWidth) || 0) : 0;
    const borderX = cs ? (parseFloat(cs.borderLeftWidth) || 0) + (parseFloat(cs.borderRightWidth) || 0) : 0;

    const options = panel ? panel.querySelectorAll(".fx-dropdown-option") : [];
    let optionHeight = 0;
    let maxOptionWidth = 0;
    for (const option of options) {
        if (!optionHeight) optionHeight = option.offsetHeight || 0;
        // Options are white-space:nowrap, so scrollWidth is the true text+padding
        // width even while the panel clips them.
        maxOptionWidth = Math.max(maxOptionWidth, option.scrollWidth || 0);
    }

    const contentHeight = panel
        ? panel.scrollHeight || panel.offsetHeight || desiredMaxHeight
        : desiredMaxHeight;
    const desiredHeight = Math.max(1, Math.min(contentHeight + borderY, desiredMaxHeight));
    const spaceBelow = Math.max(0, visibleBottom - rect.bottom - margin);
    const spaceAbove = Math.max(0, rect.top - visibleTop - margin);
    const openUp = spaceBelow < desiredHeight && spaceAbove > spaceBelow;
    const available = openUp ? spaceAbove : spaceBelow;
    let maxHeight = Math.max(36, Math.min(desiredHeight, available || desiredHeight));
    // When space clips the list, cut on a whole-option boundary instead of mid-row.
    if (optionHeight > 0 && maxHeight < contentHeight + borderY) {
        const rows = Math.max(1, Math.floor((maxHeight - borderY) / optionHeight));
        maxHeight = Math.max(36, rows * optionHeight + borderY);
    }
    const willScroll = contentHeight + borderY > maxHeight + 0.5;
    const minWidth = Math.max(0, rect.width);
    // Text-based width for hosts that opt in (grid cell editors): the widest
    // option plus panel chrome, plus the scrollbar lane when the list scrolls.
    const contentWidth = maxOptionWidth > 0
        ? Math.max(60, maxOptionWidth + borderX + (willScroll ? 14 : 0))
        : 0;
    // Fixed-position popups wider than their host (DropDownGridControl) pass their own
    // width so the right-edge clamp keeps the whole popup on screen.
    const panelWidth = panelWidthOverride > 0 ? panelWidthOverride : minWidth;
    const maxLeft = Math.max(margin, viewportWidth - margin - panelWidth);
    const left = Math.min(Math.max(margin, rect.left), maxLeft);
    const top = openUp
        ? Math.max(margin, rect.top - maxHeight + 1)
        : Math.min(Math.max(margin, rect.bottom - 1), Math.max(margin, viewportHeight - margin - maxHeight));

    return {
        openUp,
        maxHeight,
        top,
        left,
        minWidth,
        contentWidth
    };
}

// Keep layout, scroll, focus and the first visible paint in the same browser turn.
// The panel reference identifies this opening; a late call cannot reveal a replacement.
const preparedPanels = new WeakMap();
export function prepareDropdown(host, panel, editable, dotNetRef) {
    if (!host?.isConnected || !panel?.isConnected || !host.contains(panel)) return null;
    // The observer may already have revealed it. Never refocus an option after
    // the user has moved or clicked while the server acknowledgment was pending.
    if (preparedPanels.has(panel)) return preparedPanels.get(panel);
    const geometry = measureDropdown(host, 180, 8, 0, panel);
    host.classList.toggle("fx-dropdown-open-up", geometry.openUp);
    panel.style.removeProperty("top");
    panel.style.maxHeight = `${geometry.maxHeight}px`;
    if (!panel.classList.contains("fx-dropdown-panel-fit")) {
        panel.style.width = panel.style.minWidth = panel.style.maxWidth = `${geometry.minWidth}px`;
    }
    const option = panel.querySelector(".fx-dropdown-option.highlighted");
    if (option) {
        // An item out of view opens as the TOP row, as the VB6 combo drops its list
        // (top index = current item); not the bottom row, where the next ArrowDown
        // would have to scroll.
        const top = option.offsetTop;
        const bottom = top + option.offsetHeight;
        if (top < panel.scrollTop || bottom > panel.scrollTop + panel.clientHeight)
            panel.scrollTop = Math.max(0, Math.min(top, panel.scrollHeight - panel.clientHeight));
        if (!editable) option.focus({ preventScroll: true });
    }
    if (!editable) watchFocusLeave(host, dotNetRef);
    panel.style.removeProperty("opacity");
    panel.style.removeProperty("pointer-events");
    host.querySelector(".fx-dropdown-backdrop")?.style.removeProperty("visibility");
    preparedPanels.set(panel, geometry);
    return geometry;
}

const openingWatchers = new WeakMap();
export function watchDropdownOpening(host, dotNetRef) {
    if (!host) return;
    unwatchDropdownOpening(host);
    const prepare = () => {
        const panel = host.querySelector(":scope > .fx-dropdown-panel");
        if (panel) {
            const input = host.querySelector(".fx-dropdown-input");
            prepareDropdown(host, panel, !!input, dotNetRef);
            // Editable combos keep focus in the input. Scroll the active option
            // during the render batch instead of a second server/interop trip.
            const activeId = input?.getAttribute("aria-activedescendant");
            const option = activeId && document.getElementById(activeId);
            if (option && panel.contains(option)) {
                const top = option.offsetTop, bottom = top + option.offsetHeight;
                if (top < panel.scrollTop) panel.scrollTop = top;
                else if (bottom > panel.scrollTop + panel.clientHeight)
                    panel.scrollTop = bottom - panel.clientHeight;
            }
        }
        else unwatchFocusLeave(host);
    };
    const onMouseDown = event => {
        const input = host.querySelector(".fx-dropdown-input");
        if (!input || input.disabled || event.button !== 0 || !event.target.closest(".fx-dropdown-arrow")) return;
        // A span otherwise focuses the surrounding dialog, losing all list keys.
        event.preventDefault();
        input.focus({ preventScroll: true });
    };
    const onKeyDown = event => {
        if (!event.target.matches(".fx-dropdown-input")) return;
        const open = !!host.querySelector(":scope > .fx-dropdown-panel");
        if (event.key === "ArrowDown" || event.key === "ArrowUp" || event.key === "F4"
            || (open && (event.key === "Home" || event.key === "End")))
            event.preventDefault();
    };
    const observer = new MutationObserver(prepare);
    observer.observe(host, { childList: true, subtree: true, attributes: true, attributeFilter: ["aria-activedescendant"] });
    host.addEventListener("mousedown", onMouseDown);
    host.addEventListener("keydown", onKeyDown);
    openingWatchers.set(host, { observer, onMouseDown, onKeyDown });
    prepare();
}

export function unwatchDropdownOpening(host) {
    editableDropdowns.get(host)?.dispose();
    const watcher = openingWatchers.get(host);
    watcher?.observer.disconnect();
    if (watcher) {
        host.removeEventListener("mousedown", watcher.onMouseDown);
        host.removeEventListener("keydown", watcher.onKeyDown);
    }
    openingWatchers.delete(host);
    unwatchFocusLeave(host);
}

const editableDropdowns = new WeakMap();

// Blazor renders the options once, including templates and stable values. The
// browser owns presentation and caret state; only a complete commit crosses the
// circuit. Never recreate/reparent Blazor-owned DOM or refocus on a late reply.
export function enableClientEditableDropdown(host, dotNetRef) {
    if (!host || editableDropdowns.has(host)) return;
    unwatchDropdownOpening(host);
    const readConfig = () => JSON.parse(host.getAttribute("data-fx-editable-dropdown") || "null");
    let config = readConfig();
    if (!config) return;
    let input, panel, options = [], open = false, active = -1, keyboard = false;
    let draft = host.querySelector(".fx-dropdown-input")?.value ?? config.text;
    let value = config.value, dirty = draft !== config.text, composing = false;
    let sequence = config.ack, editVersion = 0, command = -1, optionsVersion = -1;
    let lastServerText = config.text, lastPointer = null, disposed = false;
    const listeners = [];
    const listen = (name, handler, capture = true) => {
        host.addEventListener(name, handler, capture);
        listeners.push([name, handler, capture]);
    };
    const setAttribute = (element, name, text) => {
        if (!element) return;
        if (text === null) { if (element.hasAttribute(name)) element.removeAttribute(name); }
        else if (element.getAttribute(name) !== text) element.setAttribute(name, text);
    };
    const scrollActive = () => {
        const option = options[active];
        if (!open || !option || !panel) return;
        const top = option.offsetTop, bottom = top + option.offsetHeight;
        if (top < panel.scrollTop) panel.scrollTop = top;
        else if (bottom > panel.scrollTop + panel.clientHeight) panel.scrollTop = bottom - panel.clientHeight;
    };
    const paint = () => {
        setAttribute(host, "data-fx-local-open", open ? "" : null);
        setAttribute(host, "data-fx-key-scope", open ? "" : null);
        setAttribute(input, "aria-expanded", open ? "true" : "false");
        setAttribute(input, "aria-activedescendant", open && options[active] ? options[active].id : null);
        for (let i = 0; i < options.length; i++) {
            setAttribute(options[i], "data-fx-local-highlight", open && i === active ? "" : null);
            setAttribute(options[i], "aria-selected", options[i].dataset.fxOptionValue === value ? "true" : "false");
        }
        if (input && input.value !== draft) input.value = draft;
        scrollActive();
    };
    const layout = () => {
        if (!open || !panel) return;
        // Measure without temporarily adding overflow to the containing dialog.
        panel.style.top = "0";
        panel.style.maxHeight = "0";
        const geometry = measureDropdown(host, 180, 8, 0, panel);
        host.classList.toggle("fx-dropdown-open-up", geometry.openUp);
        panel.style.removeProperty("top");
        panel.style.maxHeight = `${geometry.maxHeight}px`;
        panel.style.removeProperty("opacity");
        panel.style.removeProperty("pointer-events");
        if (!panel.classList.contains("fx-dropdown-panel-fit"))
            panel.style.width = panel.style.minWidth = panel.style.maxWidth = `${geometry.minWidth}px`;
        paint();
    };
    const selectedIndex = () => options.findIndex(option => option.dataset.fxOptionValue === value);
    const setOpen = next => {
        open = !!next && config.enabled && !!panel;
        keyboard = false;
        if (open) {
            active = Math.max(0, selectedIndex());
            paint();
            layout();
            // Open with the current option as the first visible row, as VB6 does.
            if (options[active]) panel.scrollTop = options[active].offsetTop;
        } else paint();
    };
    const send = (kind, event, option = null, wasOpen = open, edited = dirty || (!!option && keyboard)) => {
        const preserveDraft = dirty && wasOpen && (kind === "cancel" || kind === "close");
        const picked = option ? { value: option.dataset.fxOptionValue, text: option.dataset.fxOptionText } : null;
        if (picked) { draft = picked.text; value = picked.value; }
        const version = ++editVersion, request = ++sequence;
        const text = draft;
        dirty = preserveDraft;
        if (input) input.setCustomValidity("");
        paint();
        dotNetRef.invokeMethodAsync("OnClientDropdownActionAsync", {
            sequence: request, optionsVersion: optionsVersion, kind, text,
            value: picked?.value ?? null, wasOpen, edited, key: event?.key || "",
            shiftKey: !!event?.shiftKey, ctrlKey: !!event?.ctrlKey,
            altKey: !!event?.altKey, metaKey: !!event?.metaKey
        }).then(result => {
            if (disposed || !host.isConnected || sequence !== request || editVersion !== version) return;
            if (!result.accepted) {
                dirty = true;
                input?.setCustomValidity("This choice is no longer available. Choose again.");
                input?.reportValidity();
                return;
            }
            if (preserveDraft) return;
            draft = result.text;
            value = result.value ?? readConfig()?.value ?? value;
            paint();
        }).catch(error => {
            if (disposed || !host.isConnected || sequence !== request || editVersion !== version) return;
            dirty = true;
            input?.setCustomValidity("Unable to save this value. Try again when connected.");
            input?.reportValidity();
            console.error("Editable dropdown commit failed", error);
        });
    };
    const currentChoice = () => {
        if (keyboard) return options[active];
        const selected = options[selectedIndex()];
        return !dirty && selected?.dataset.fxOptionText === draft ? selected : null;
    };
    const finish = (event, navigation = false, pick = currentChoice()) => {
        const wasOpen = open;
        const edited = dirty || (!!pick && keyboard);
        setOpen(false);
        const kind = navigation ? "navigate" : pick ? "select" : event?.type === "blur" ? "blur" : "text";
        send(kind, event, pick, wasOpen, edited);
    };
    const refresh = () => {
        const next = readConfig();
        if (!next) { binding.dispose(); return; }
        const previous = options[active]?.dataset.fxOptionValue;
        config = next;
        input = host.querySelector(".fx-dropdown-input");
        panel = host.querySelector(":scope > .fx-dropdown-panel");
        options = panel ? Array.from(panel.querySelectorAll(".fx-dropdown-option")) : [];
        if (config.optionsVersion !== optionsVersion) {
            optionsVersion = config.optionsVersion;
            active = options.findIndex(option => option.dataset.fxOptionValue === previous);
            if (active < 0) { active = Math.max(0, selectedIndex()); keyboard = false; }
        }
        if (config.ack >= sequence && !dirty && !open && config.text !== lastServerText) {
            draft = config.text;
            value = config.value;
        }
        lastServerText = config.text;
        if (!config.enabled) setOpen(false);
        if (config.command !== command) {
            command = config.command;
            setOpen(config.open);
            if (open && config.initialText) {
                const prefix = config.initialText.toLocaleLowerCase();
                active = options.findIndex(option => option.dataset.fxOptionText.toLocaleLowerCase().startsWith(prefix));
                keyboard = active >= 0;
            }
        }
        paint();
        if (open) layout();
    };
    const move = (index, event) => {
        if (!options.length) return;
        active = Math.max(0, Math.min(options.length - 1, index));
        keyboard = true;
        if (open) paint();
        else { send("select", event, options[active], false); keyboard = false; }   // a closed list has no live highlight
    };
    listen("mousedown", event => {
        if (!config.enabled || event.button !== 0) return;
        if (event.target.closest(".fx-dropdown-arrow")) {
            event.preventDefault();
            event.stopImmediatePropagation();
            input?.focus({ preventScroll: true });
        } else if (open && panel?.contains(event.target)) {
            event.preventDefault();
            event.stopImmediatePropagation();
        }
    });
    listen("click", event => {
        if (!config.enabled) return;
        const target = event.target;
        const option = target.closest(".fx-dropdown-option");
        if (target.closest(".fx-dropdown-arrow")) {
            event.preventDefault(); event.stopImmediatePropagation();
            input?.focus({ preventScroll: true });
            if (open && (dirty || keyboard)) finish(event);
            else if (open) { const wasOpen = open; setOpen(false); send("close", event, null, wasOpen); }
            else setOpen(true);
        } else if (open && option && panel.contains(option)) {
            event.preventDefault(); event.stopImmediatePropagation();
            finish(event, false, option);
        } else if (target.closest(".fx-dropdown-backdrop")) {
            event.preventDefault(); event.stopImmediatePropagation();
            if (dirty || keyboard) finish(event);
            else { setOpen(false); send("close", event, null, true); }
        }
    });
    listen("mouseover", event => {
        if (panel?.contains(event.target)) event.stopImmediatePropagation();
    });
    listen("mousemove", event => {
        if (!open || (lastPointer?.x === event.clientX && lastPointer?.y === event.clientY)) return;
        lastPointer = { x: event.clientX, y: event.clientY };
        const index = options.indexOf(event.target.closest(".fx-dropdown-option"));
        if (index >= 0) { active = index; paint(); }
    });
    listen("input", event => {
        if (event.target !== input) return;
        draft = input.value; dirty = true; keyboard = false; editVersion++;
        input.setCustomValidity("");
        if (open && draft) {
            const prefix = draft.toLocaleLowerCase();
            active = options.findIndex(option => option.dataset.fxOptionText.toLocaleLowerCase().startsWith(prefix)
                || option.dataset.fxOptionValue.toLocaleLowerCase().startsWith(prefix));
            paint();
        }
        if (!config.liveText) event.stopImmediatePropagation();
    });
    listen("compositionstart", () => { composing = true; });
    listen("compositionend", () => { composing = false; });
    listen("focus", event => { if (event.target === input) event.stopImmediatePropagation(); });
    listen("blur", event => {
        if (event.target !== input) return;
        event.stopImmediatePropagation();
        if (composing || host.contains(event.relatedTarget)) return;
        // A host that stages every keystroke (stagesText) has already fed the typed text back as
        // the control's value, so "dirty" can be false for a real edit — typed before this handler
        // attached, or cleared. Always hand such a host the blur; the server commits it either way.
        if (config.commitOnBlur && (dirty || (open && keyboard))) finish(event);
        // No visible edit, but a host that stages keystrokes may hold text the server never saw as a
        // change — hand it the blur as a plain commit of the draft, never as a pick: a closed list
        // keeps its last arrow highlight, and turning a blur into a SELECT would re-fire the value.
        else if (config.commitOnBlur && config.stagesText) finish(event, false, null);
        else if (open) { setOpen(false); send("close", event, null, true); }
        else if (!config.commitOnBlur) { draft = config.text; dirty = false; paint(); }
    });
    listen("keydown", event => {
        if (event.target !== input || !config.enabled) return;
        if (composing || event.isComposing || event.keyCode === 229) { event.stopImmediatePropagation(); return; }
        const key = event.key;
        const navigation = config.gridKeys || config.hosted;
        if (key === "F4" || (event.altKey && key === "ArrowDown")) {
            event.preventDefault(); event.stopImmediatePropagation();
            if (open) finish(event); else setOpen(true);
        } else if (open && event.altKey && key === "ArrowUp") {
            event.preventDefault(); event.stopImmediatePropagation(); finish(event);
        } else if (key === "ArrowDown" || key === "ArrowUp") {
            event.preventDefault(); event.stopImmediatePropagation();
            if (!open && config.delegateArrows) finish(event, true);
            else move((open ? active : selectedIndex()) + (key === "ArrowDown" ? 1 : -1), event);
        } else if (open && ["Home", "End", "PageUp", "PageDown"].includes(key)) {
            event.preventDefault(); event.stopImmediatePropagation();
            const page = Math.max(1, Math.floor(panel.clientHeight / (options[active]?.offsetHeight || 18)));
            move(key === "Home" ? 0 : key === "End" ? options.length - 1 : active + (key === "PageDown" ? page : -page), event);
        } else if (key === "Enter" || key === "NumpadEnter") {
            event.preventDefault(); event.stopImmediatePropagation();
            finish(event, !open && navigation);
        } else if (key === "Tab") {
            event.stopImmediatePropagation();
            const delegate = config.gridKeys || (config.hosted && !open) || (config.forwardKeys && !config.hosted);
            if (config.gridKeys || (config.hosted && !open)) event.preventDefault();
            if (dirty || open || delegate) finish(event, delegate);
        } else if (key === "Escape") {
            event.preventDefault(); event.stopImmediatePropagation();
            const wasOpen = open;
            setOpen(false); send("cancel", event, null, wasOpen);
        } else if (config.gridKeys && !open && !event.shiftKey && !event.ctrlKey && !event.metaKey
            && input.selectionStart === input.selectionEnd
            && ((key === "ArrowLeft" && input.selectionStart === 0)
                || (key === "ArrowRight" && input.selectionEnd === input.value.length))) {
            event.preventDefault(); event.stopImmediatePropagation(); finish(event, true);
        } else if (!event.altKey && !event.ctrlKey && !event.metaKey) event.stopImmediatePropagation();
    });
    const observer = new MutationObserver(refresh);
    const binding = { dispose() {
        disposed = true; observer.disconnect();
        for (const args of listeners) host.removeEventListener(...args);
        editableDropdowns.delete(host);
        host.removeAttribute("data-fx-local-ready");
        host.removeAttribute("data-fx-local-open");
    } };
    editableDropdowns.set(host, binding);
    host.setAttribute("data-fx-local-ready", "");
    observer.observe(host, { childList: true, subtree: true, attributes: true,
        attributeFilter: ["data-fx-editable-dropdown", "value", "disabled"] });
    refresh();
}


// Focus leaving an OPEN list: one registration per open, judged after the
// browser has settled the new focus — a move between the list's own options is
// not a leave, and a window that lost focus (alt-tab) keeps its list. The
// control decides what leaving means (commit the highlighted option / close).
const focusLeaveWatchers = new WeakMap();
export function watchFocusLeave(host, dotNetRef) {
    if (!host || !dotNetRef || focusLeaveWatchers.has(host)) return;
    const onFocusOut = () => {
        setTimeout(() => {
            if (!focusLeaveWatchers.has(host)) return;
            const doc = host.ownerDocument;
            if (!doc.hasFocus()) return;
            const active = doc.activeElement;
            if (active && host.contains(active)) return;
            dotNetRef.invokeMethodAsync("OnListFocusLeftAsync").catch(() => { });
        }, 0);
    };
    host.addEventListener("focusout", onFocusOut);
    focusLeaveWatchers.set(host, onFocusOut);
}
export function unwatchFocusLeave(host) {
    const onFocusOut = host && focusLeaveWatchers.get(host);
    if (!onFocusOut) return;
    host.removeEventListener("focusout", onFocusOut);
    focusLeaveWatchers.delete(host);
}
