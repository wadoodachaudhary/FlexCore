// DrawingControl — the ONLY JavaScript the drawing surface uses (last resort, per the
// limit-JS rule). All interaction (tools, create, select, move, text edit, zoom, undo) is pure
// Blazor/C#; this module just (a) reports an <img>'s natural pixel size (load-aware) and
// (b) rasterises the C#-composed SVG (background + shapes) into a PNG data URL on save.
// ES module, lazily imported via ./_content/FlexKit/drawing-control.js.

export function naturalSize(el) {
    return new Promise((resolve) => {
        if (!el) { resolve([0, 0]); return; }
        if (el.complete && el.naturalWidth) { resolve([el.naturalWidth, el.naturalHeight]); return; }
        el.addEventListener('load', () => resolve([el.naturalWidth || 0, el.naturalHeight || 0]), { once: true });
        el.addEventListener('error', () => resolve([0, 0]), { once: true });
    });
}

// Enter = commit+close the bubble editor; Shift+Enter = newline. Pure Blazor can't
// conditionally preventDefault per key, so we do it here: on Enter-without-Shift we stop the
// newline and blur the textarea, which fires its Blazor @onblur → commit. Idempotent per element.
export function attachEditorEnter(el) {
    if (!el || el.__fxEnterBound) return;
    el.__fxEnterBound = true;
    el.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();   // no newline inserted
            el.blur();            // → Blazor @onblur → StopEditing (save + close)
        }
        // Shift+Enter (and everything else) falls through to default behaviour.
    });
}

// Inner (content) size of an element — used to auto-fit the image to the scroll viewport.
export function clientSize(el) {
    if (!el) return [0, 0];
    return [el.clientWidth || 0, el.clientHeight || 0];
}

// Crop a sub-rectangle (image px) out of a data/URL image into a new PNG data URL. Canvas
// drawImage with a source rect; the source is a same-origin data URL so the canvas isn't tainted.
export function cropImage(srcUrl, sx, sy, sw, sh) {
    return new Promise((resolve) => {
        try {
            const img = new Image();
            img.onload = () => {
                try {
                    const nx = Math.max(0, Math.min(sx, img.naturalWidth));
                    const ny = Math.max(0, Math.min(sy, img.naturalHeight));
                    const nw = Math.max(1, Math.min(sw, img.naturalWidth - nx));
                    const nh = Math.max(1, Math.min(sh, img.naturalHeight - ny));
                    const canvas = document.createElement('canvas');
                    canvas.width = Math.round(nw);
                    canvas.height = Math.round(nh);
                    const ctx = canvas.getContext('2d');
                    ctx.drawImage(img, nx, ny, nw, nh, 0, 0, canvas.width, canvas.height);
                    resolve(canvas.toDataURL('image/png'));
                } catch (e) { console.error('DrawingControl.cropImage draw failed', e); resolve(''); }
            };
            img.onerror = (e) => { console.error('DrawingControl.cropImage load failed', e); resolve(''); };
            img.src = srcUrl;
        } catch (e) { console.error('DrawingControl.cropImage failed', e); resolve(''); }
    });
}

// Natural pixel size of a data/URL image that is NOT in the DOM — used to size a placed overlay
// to its real aspect ratio. Resolves [0,0] on failure.
export function imageSize(srcUrl) {
    return new Promise((resolve) => {
        try {
            const img = new Image();
            img.onload = () => resolve([img.naturalWidth || 0, img.naturalHeight || 0]);
            img.onerror = () => resolve([0, 0]);
            img.src = srcUrl;
        } catch (e) { resolve([0, 0]); }
    });
}

// Read an image off the clipboard as a data URL ('' if none / denied). Requires a user gesture and
// a secure context (https or localhost). Pure capability — no app coupling.
export function readClipboardImage() {
    return new Promise(async (resolve) => {
        try {
            if (!navigator.clipboard || !navigator.clipboard.read) { resolve(''); return; }
            const items = await navigator.clipboard.read();
            for (const item of items) {
                const type = (item.types || []).find(t => t.startsWith('image/'));
                if (type) {
                    const blob = await item.getType(type);
                    const reader = new FileReader();
                    reader.onload = () => resolve(reader.result || '');
                    reader.onerror = () => resolve('');
                    reader.readAsDataURL(blob);
                    return;
                }
            }
            resolve('');
        } catch (e) { resolve(''); }
    });
}

export function rasterize(svg, w, h) {
    return new Promise((resolve) => {
        try {
            const img = new Image();
            img.onload = () => {
                try {
                    const canvas = document.createElement('canvas');
                    canvas.width = Math.max(1, Math.round(w));
                    canvas.height = Math.max(1, Math.round(h));
                    const ctx = canvas.getContext('2d');
                    ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
                    resolve(canvas.toDataURL('image/png'));
                } catch (e) { console.error('DrawingControl.rasterize draw failed', e); resolve(''); }
            };
            img.onerror = (e) => { console.error('DrawingControl.rasterize svg load failed', e); resolve(''); };
            img.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg);
        } catch (e) { console.error('DrawingControl.rasterize failed', e); resolve(''); }
    });
}
