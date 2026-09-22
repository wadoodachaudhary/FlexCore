export function capturePointer(element, pointerId) {
    if (element?.isConnected) element.setPointerCapture(pointerId);
}

const canvasKeys = new WeakMap();
export function bindCanvasKeyboard(element) {
    if (!element || canvasKeys.has(element)) return;
    const listener = event => {
        if (event.target !== element && !event.target.closest('.fx-rd-object')) return;
        if (event.key?.startsWith('Arrow') || event.key === 'Delete' ||
            (event.ctrlKey || event.metaKey) && ['a', 'c', 'x', 'v', 'd', 'z', 'y'].includes((event.key || '').toLowerCase())) event.preventDefault();
    };
    canvasKeys.set(element, listener);
    element.addEventListener('keydown', listener);
}
export function unbindCanvasKeyboard(element) {
    const listener = canvasKeys.get(element);
    if (listener) element.removeEventListener('keydown', listener);
    canvasKeys.delete(element);
}

// Font shaping/wrapping belongs to the browser; C# owns all band/page placement.
export async function measureText(items) {
    return (await measureTextLayout(items)).map(item => item.height);
}

export async function measureTextLayout(items) {
    await document.fonts.ready;
    const host = document.createElement('div');
    host.style.cssText = 'position:fixed;left:-100000px;top:0;visibility:hidden;pointer-events:none;contain:layout style;';
    document.body.append(host);
    try {
        const nodes = items.map(item => {
            const node = document.createElement('div');
            node.style.cssText = item.style + ';position:relative;height:auto;min-height:0;max-height:none;';
            node.innerHTML = item.html;
            host.append(node);
            return node;
        });
        return nodes.map(node => {
            const bounds = node.getBoundingClientRect();
            const rectangles = [];
            const walker = document.createTreeWalker(node, NodeFilter.SHOW_TEXT);
            const range = document.createRange();
            const textNodes = [];
            let text = '';
            while (walker.nextNode()) {
                textNodes.push({ node: walker.currentNode, start: text.length });
                text += walker.currentNode.textContent;
            }
            if (text.length > 1000000) throw new Error('Report text exceeds the 1,000,000-character measurement limit.');
            let first = 0, last = 0;
            // UTF-16 offsets match .NET strings; grapheme boundaries keep combined glyphs intact.
            for (const part of new Intl.Segmenter(undefined, { granularity: 'grapheme' }).segment(text)) {
                const start = part.index, end = start + part.segment.length;
                while (first + 1 < textNodes.length && textNodes[first + 1].start <= start) first++;
                while (last + 1 < textNodes.length && textNodes[last + 1].start < end) last++;
                range.setStart(textNodes[first].node, start - textNodes[first].start);
                range.setEnd(textNodes[last].node, end - textNodes[last].start);
                for (const rect of range.getClientRects()) {
                    if (rect.height > 0) rectangles.push({
                        top: Math.max(0, Math.floor((rect.top - bounds.top) * 15)),
                        bottom: Math.ceil((rect.bottom - bounds.top) * 15), start, end
                    });
                }
            }
            rectangles.sort((a, b) => a.top - b.top || a.bottom - b.bottom);
            const lines = [];
            for (const rect of rectangles) {
                const last = lines[lines.length - 1];
                if (last && rect.top < last.bottom) {
                    last.bottom = Math.max(last.bottom, rect.bottom);
                    last.start = Math.min(last.start, rect.start);
                    last.end = Math.max(last.end, rect.end);
                }
                else lines.push(rect);
            }
            // Include invisible wrapping whitespace in the preceding line's consumed prefix.
            for (let i = 0; i < lines.length; i++) lines[i].end = i + 1 < lines.length ? lines[i + 1].start : text.length;
            return { height: Math.ceil(bounds.height * 15), lines };
        });
    } finally {
        host.remove();
    }
}
