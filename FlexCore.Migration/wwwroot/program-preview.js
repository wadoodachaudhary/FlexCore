// Maps a browser pointer into framebuffer pixels and focuses the preview surface.
export function imagePoint(img, clientX, clientY) {
    if (!img || !img.getBoundingClientRect) return null;
    const rect = img.getBoundingClientRect();
    const width = img.naturalWidth || img.width;
    const height = img.naturalHeight || img.height;
    if (!rect.width || !rect.height || !width || !height) return null;
    return {
        x: (clientX - rect.left) * width / rect.width,
        y: (clientY - rect.top) * height / rect.height
    };
}

export function focus(el) {
    if (el && el.focus) el.focus();
}
