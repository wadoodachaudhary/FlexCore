export async function copyText(text) {
    if (navigator.clipboard && navigator.clipboard.writeText) {
        await navigator.clipboard.writeText(text ?? "");
        return true;
    }
    return false;
}
