// Scroll-sync for ConverterEditor. The gutter is not an input; it follows the textarea.
export function bindEditor(root) {
    if (!root) return { dispose() {} };
    const gutter = root.querySelector(".fx-converter-gutter");
    const area = root.querySelector("textarea");
    if (!gutter || !area) return { dispose() {} };
    const sync = () => { gutter.scrollTop = area.scrollTop; };
    area.addEventListener("scroll", sync, { passive: true });
    return {
        dispose() { area.removeEventListener("scroll", sync); }
    };
}

export async function copyText(text) {
    if (navigator.clipboard && navigator.clipboard.writeText) {
        await navigator.clipboard.writeText(text ?? "");
        return true;
    }
    return false;
}
