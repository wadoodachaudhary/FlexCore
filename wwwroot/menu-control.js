function menuItems(panel) {
    if (!panel) return [];
    return Array.from(panel.querySelectorAll("button[role='menuitem'], [role='menuitem']"))
        .filter(el => !el.disabled && el.getAttribute("aria-disabled") !== "true" && el.offsetParent !== null);
}

function setKeyboardMode(panel, keyboardActive) {
    panel?.classList.toggle("fx-menu-keyboard-active", keyboardActive === true);
}

export function clearKeyboardMode(panel) {
    setKeyboardMode(panel, false);
}

export function focusFirstMenuItem(panel, keyboardActive = false) {
    setKeyboardMode(panel, keyboardActive);
    const items = menuItems(panel);
    items[0]?.focus();
}

export function focusLastMenuItem(panel, keyboardActive = false) {
    setKeyboardMode(panel, keyboardActive);
    const items = menuItems(panel);
    items[items.length - 1]?.focus();
}

export function focusAdjacentMenuItem(panel, direction, keyboardActive = false) {
    setKeyboardMode(panel, keyboardActive);
    const items = menuItems(panel);
    if (items.length === 0) return;

    const active = document.activeElement;
    const index = items.indexOf(active);
    const next = index < 0
        ? (direction > 0 ? 0 : items.length - 1)
        : (index + direction + items.length) % items.length;
    items[next]?.focus();
}

export function clickFocusedMenuItem(panel) {
    const items = menuItems(panel);
    if (items.includes(document.activeElement)) {
        document.activeElement.click();
    }
}
