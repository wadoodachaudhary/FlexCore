// Focus handling for popups: on open, move focus INTO the dialog (text inputs
// first, else any keyboard-focusable element such as a grid host); hold it
// there briefly against the caller's own "refocus after click" which can land
// after the dialog opened; on close, give focus BACK to the caller.
const focusReturnStack = [];

// Recent focus history (newest last). captureFocus() runs one interop round
// trip behind the dialog's render, and dialog content (e.g. FPickList's own
// grid) often focuses itself in the same breath the dialog appears — by
// capture time document.activeElement is INSIDE the dialog, and recording it
// meant restoreFocus popped a dead element on close and the keyboard fell to
// <body> (the "Esc doesn't bring focus back to the grid" trap). The history
// lets capture reach back to who really held focus before the dialog did.
const focusHistory = [];
document.addEventListener("focusin", e => {
    const t = e.target;
    if (!t || t === document.body) return;
    const last = focusHistory[focusHistory.length - 1];
    if (last === t) return;
    focusHistory.push(t);
    if (focusHistory.length > 10) focusHistory.shift();
}, true);

function pickFocusTarget(root) {
    // Text inputs first; then keyboard-focusable content (a grid host);
    // buttons LAST and never the dialog's own chrome (close / window icon).
    return root.querySelector(
            "input:not([type=hidden]):not(:disabled), textarea:not(:disabled), select:not(:disabled), [contenteditable=true]")
        || root.querySelector("[tabindex='0']")
        || root.querySelector(".fx-dialog-body button:not(:disabled), .fx-dialog-footer button:not(:disabled)")
        || root;
}

// Steal guard: the control under the dialog often refocuses itself at the
// end of the very click that opened the popup — that async refocus lands
// AFTER ours and sends the keyboard back to the caller. For a short window
// any focus that leaves the dialog is pulled back in.
function guardFocusWithin(root, preferred) {
    const guard = () => {
        if (!root.isConnected) { document.removeEventListener("focusin", guard, true); return; }
        if (root.contains(document.activeElement)) return;
        // Recover to whatever actually held focus, not to the generic pick:
        // a claimed grid sits behind any text input in the same dialog.
        const target = preferred && preferred.isConnected && root.contains(preferred)
            ? preferred
            : pickFocusTarget(root);
        target.focus({ preventScroll: true });
    };
    document.addEventListener("focusin", guard, true);
    setTimeout(() => document.removeEventListener("focusin", guard, true), 600);
}

export function focusFirst(root) {
    if (!root) return;
    captureFocus(root);
    focusContent(root);
}

// Focus pass WITHOUT recording the caller — a second attempt after the caller
// was already captured.
export function focusContent(root) {
    if (!root) return;
    // The initial focus pass arrives after a server round trip. A user may
    // already be typing in another field; preserve that field and its caret.
    const active = document.activeElement;
    const enteredField = root.contains(active)
        && active.matches("input, textarea, select, [contenteditable=true]");
    const target = enteredField ? active : pickFocusTarget(root);
    if (target !== active) target.focus({ preventScroll: true });
    guardFocusWithin(root, target);
}

// Guard only: the dialog's content already took focus for itself. Returns
// whether it really did — an unrendered or hidden claimant focuses nothing,
// and FocusAsync on it raises no error.
export function holdFocusWithin(root) {
    if (!root) return false;
    const claimed = root.contains(document.activeElement) ? document.activeElement : null;
    guardFocusWithin(root, claimed);
    return claimed != null;
}

// True when the popup carries a text entry of its own. Entries INSIDE a
// keyboard-navigable container ([tabindex='0'], e.g. a grid host and its cell
// editors / filter inputs) belong to that container, not to the popup.
export function hasOwnTextEntry(root) {
    if (!root) return false;
    const sel = "input:not([type=hidden]):not([type=checkbox]):not([type=radio]):not(:disabled), "
        + "textarea:not(:disabled), select:not(:disabled), [contenteditable=true]";
    return Array.from(root.querySelectorAll(sel)).some(el => !el.closest("[tabindex='0']"));
}


// Capture-only variant: dialogs that manage their own focus (AutoFocus=false)
// still record the caller so restoreFocus can give focus back on close.
// `root` is the opening dialog's own element: anything focused inside it is
// the dialog's content, never the caller, so the capture reaches back through
// the focus history for the most recent holder OUTSIDE that dialog. Nested
// popups stay correct — a message box over a picklist captures the picklist's
// grid (outside the MESSAGE BOX's root), not the page underneath both.
export function captureFocus(root) {
    const prev = document.activeElement;
    if (prev && prev !== document.body && !(root && root.contains(prev))) {
        focusReturnStack.push(prev);
        return;
    }
    for (let i = focusHistory.length - 1; i >= 0; i--) {
        const el = focusHistory[i];
        if (el && el.isConnected && el !== document.body && !(root && root.contains(el))) {
            focusReturnStack.push(el);
            return;
        }
    }
    focusReturnStack.push(null);
}

export function restoreFocus() {
    const el = focusReturnStack.pop();
    if (el && el.isConnected) {
        try { el.focus({ preventScroll: true }); return; } catch { /* detached mid-close */ }
    }
    // The captured caller died while the dialog was open (re-render replaced
    // it, or the capture found nothing). Falling back to the most recent
    // surviving holder beats stranding the keyboard on <body>.
    for (let i = focusHistory.length - 1; i >= 0; i--) {
        const fb = focusHistory[i];
        if (fb && fb.isConnected && fb !== document.body
            && !fb.closest(".fx-dialog, .fx-msgbox-overlay")) {
            try { fb.focus({ preventScroll: true }); } catch { /* detached */ }
            return;
        }
    }
}

// Focus return for a popup rendered INSIDE another control: back to the
// keyboard host that owns the opener (a grid host and the like), else to the
// opener itself. document.activeElement at open time cannot be used for this —
// an opener that preventDefaults its mousedown leaves it on <body> or on an
// unrelated control that then steals the return.
export function focusPopupOwner(opener) {
    if (!opener) return;
    const target = opener.parentElement?.closest("[tabindex]:not([tabindex='-1'])") || opener;
    if (target.isConnected) {
        try { target.focus({ preventScroll: true }); } catch { /* detached mid-close */ }
    }
}

// Placement for a popup that is position:absolute inside its host: EVERY
// ancestor with a non-visible overflow clips it, so the flip has to be measured
// against the intersection of those clip rects. Measured against the viewport
// alone the popup opens into a scroll container's overflow, where it only
// extends that container's scrollHeight and stays invisible.
export function measurePopupPlacement(host, panel) {
    if (!host || !panel) return { dropUp: false, alignRight: false };

    const hostRect = host.getBoundingClientRect();
    const width = panel.offsetWidth;
    const height = panel.offsetHeight;
    let top = 0;
    let left = 0;
    let bottom = window.innerHeight || document.documentElement.clientHeight || 0;
    let right = window.innerWidth || document.documentElement.clientWidth || 0;

    for (let el = host.parentElement; el && el !== document.body && el !== document.documentElement; el = el.parentElement) {
        const style = window.getComputedStyle(el);
        const clipsY = (style.overflowY || "").toLowerCase() !== "visible";
        const clipsX = (style.overflowX || "").toLowerCase() !== "visible";
        if (!clipsY && !clipsX) continue;
        const rect = el.getBoundingClientRect();
        if (clipsY) {
            top = Math.max(top, rect.top);
            bottom = Math.min(bottom, rect.bottom);
        }
        if (clipsX) {
            left = Math.max(left, rect.left);
            right = Math.min(right, rect.right);
        }
    }

    const spaceBelow = bottom - hostRect.bottom;
    const spaceAbove = hostRect.top - top;
    const dropUp = spaceBelow < height && spaceAbove > spaceBelow;

    // When NEITHER side fits, flipping only picks the less-bad side and the
    // popup still overruns its clip box, where the overflow is silently cut
    // off. Cap it to the space actually available so it scrolls instead.
    const available = Math.floor((dropUp ? spaceAbove : spaceBelow) - 4);
    return {
        dropUp,
        alignRight: hostRect.left + width > right && hostRect.right - width >= left,
        maxHeight: height > available ? Math.max(available, 60) : 0
    };
}

// Placement is measured once on open, so a viewport change while the popup is
// open leaves it positioned against the old geometry. One listener per open
// popup, coalesced to an animation frame because resize fires continuously
// during a drag.
let placementWatch = null;

export function watchPopupPlacement(dotNetRef) {
    unwatchPopupPlacement();
    let queued = false;
    const onChange = () => {
        if (queued) return;
        queued = true;
        requestAnimationFrame(() => {
            queued = false;
            if (!placementWatch) return;
            dotNetRef.invokeMethodAsync("RemeasurePlacementAsync").catch(() => unwatchPopupPlacement());
        });
    };
    window.addEventListener("resize", onChange);
    placementWatch = onChange;
}

export function unwatchPopupPlacement() {
    if (!placementWatch) return;
    window.removeEventListener("resize", placementWatch);
    placementWatch = null;
}

// Escape / Ctrl+Enter / Tab for the dialog, listened in the CAPTURE phase so
// they work even when the focused control stops keydown propagation
// (TextBoxControl does, for its grid-embedded editors). The listener dies with
// the element.
const DIALOG_TABBABLE =
    "input:not([type=hidden]):not(:disabled), textarea:not(:disabled), " +
    "select:not(:disabled), button:not(:disabled), [tabindex]:not([tabindex='-1'])";

export function registerDialogKeys(root, dotNetRef) {
    if (!root) return;
    root.addEventListener("keydown", e => {
        // A popup inside the dialog that owns its own keys (its own Escape /
        // Tab) opts out — this listener is capture-phase, so a bubble-phase
        // stopPropagation cannot reach it.
        if (e.target instanceof Element && e.target.closest("[data-fx-key-scope]")) return;
        if (e.key === "Escape" && !e.altKey && !e.ctrlKey && !e.metaKey && !e.shiftKey) {
            dotNetRef.invokeMethodAsync("OnDialogContentEscapeAsync");
        } else if ((e.key === "Enter" || e.key === "NumpadEnter") && e.ctrlKey) {
            dotNetRef.invokeMethodAsync("OnDialogContentCtrlEnterAsync");
        } else if (e.key === "Tab" && !e.altKey && !e.ctrlKey && !e.metaKey) {
            // Modal tab loop: like a VB6 form, Tab cycles the dialog's own
            // controls and never wanders into the covered page behind the
            // overlay (where focus becomes invisible and Tab looks dead).
            const active = document.activeElement;
            if (active?.dataset?.fxTabCapture) return; // TabSpaces editor owns Tab
            const tabbables = [...root.querySelectorAll(DIALOG_TABBABLE)]
                .filter(el => !el.closest(".fx-dialog-header") && el.offsetParent !== null);
            if (!tabbables.length) return;
            const first = tabbables[0], last = tabbables[tabbables.length - 1];
            const inCycle = tabbables.includes(active);
            if (!e.shiftKey && (active === last || !inCycle)) {
                e.preventDefault();
                first.focus();
            } else if (e.shiftKey && (active === first || !inCycle)) {
                e.preventDefault();
                last.focus();
            }
        }
    }, true);
}
