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

function getTextValue(element) {
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
    const next = value.substring(0, selection.start) + text + value.substring(selection.end);
    const caret = selection.start + text.length;
    element.value = next;
    setTextSelection(element, caret);
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
