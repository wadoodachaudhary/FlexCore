// Focus handling for popups: on open, move focus INTO the dialog (text inputs
// first, else any keyboard-focusable element such as a grid host); hold it
// there briefly against the caller's own "refocus after click" which can land
// after the dialog opened; on close, give focus BACK to the caller.
const focusReturnStack = [];

function pickFocusTarget(root) {
    // Text inputs first; then keyboard-focusable content (a grid host);
    // buttons LAST and never the dialog's own chrome (close / window icon).
    return root.querySelector(
            "input:not([type=hidden]):not(:disabled), textarea:not(:disabled), select:not(:disabled), [contenteditable=true]")
        || root.querySelector("[tabindex='0']")
        || root.querySelector(".fx-dialog-body button:not(:disabled), .fx-dialog-footer button:not(:disabled)")
        || root;
}

export function focusFirst(root) {
    if (!root) return;
    const prev = document.activeElement;
    focusReturnStack.push(prev && prev !== document.body ? prev : null);
    pickFocusTarget(root).focus({ preventScroll: true });

    // Steal guard: the control under the dialog often refocuses itself at the
    // end of the very click that opened the popup — that async refocus lands
    // AFTER ours and sends the keyboard back to the caller. For a short window
    // any focus that leaves the dialog is pulled back in.
    const guard = () => {
        if (!root.isConnected) { document.removeEventListener("focusin", guard, true); return; }
        if (!root.contains(document.activeElement)) pickFocusTarget(root).focus({ preventScroll: true });
    };
    document.addEventListener("focusin", guard, true);
    setTimeout(() => document.removeEventListener("focusin", guard, true), 600);
}


// Capture-only variant: dialogs that manage their own focus (AutoFocus=false)
// still record the caller so restoreFocus can give focus back on close.
export function captureFocus() {
    const prev = document.activeElement;
    focusReturnStack.push(prev && prev !== document.body ? prev : null);
}

export function restoreFocus() {
    const el = focusReturnStack.pop();
    if (el && el.isConnected) {
        try { el.focus({ preventScroll: true }); } catch { /* detached mid-close */ }
    }
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
