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
            const limit = item.maxNumberOfLines ?? 0;
            if (!Number.isInteger(limit) || limit < 0) throw new Error('Invalid maximum report field line count.');
            if (limit > 0) {
                // The actual browser font/line box bounds growth before C# places bands.
                // Keep the complete text in the DOM; the final measured box clips its tail.
                const style = getComputedStyle(node);
                const lineHeight = parseFloat(style.lineHeight);
                if (!Number.isFinite(lineHeight) || lineHeight <= 0) throw new Error('Report field line height is unavailable.');
                const edges = parseFloat(style.borderTopWidth) + parseFloat(style.borderBottomWidth)
                    + parseFloat(style.paddingTop) + parseFloat(style.paddingBottom);
                node.style.maxHeight = `${lineHeight * limit + edges}px`;
            }
            return node;
        });
        // Fonts may only be requested when the first report field enters the DOM.
        // Measuring before these loads finish permanently paginates with fallback widths.
        const fonts = new Map();
        for (const node of nodes) for (const run of [node, ...node.querySelectorAll('span')]) {
            const style = getComputedStyle(run);
            const font = `${style.fontStyle} ${style.fontWeight} ${style.fontSize} ${style.fontFamily}`;
            fonts.set(font, (fonts.get(font) || '') + run.textContent);
        }
        await Promise.all([...fonts].map(([font, text]) => document.fonts.load(font, text)));
        return nodes.map((node, index) => {
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
                // Glyph boxes can overlap the next line slightly (Arial 9pt at
                // 1.15 line height does). Only substantial overlap joins glyphs
                // on the same line, including mixed font sizes on one baseline.
                const overlap = last ? Math.min(last.bottom, rect.bottom) - Math.max(last.top, rect.top) : 0;
                if (last && overlap > Math.min(last.bottom - last.top, rect.bottom - rect.top) / 2) {
                    last.bottom = Math.max(last.bottom, rect.bottom);
                    last.start = Math.min(last.start, rect.start);
                    last.end = Math.max(last.end, rect.end);
                }
                else lines.push(rect);
            }
            // Include invisible wrapping whitespace in the preceding line's consumed prefix.
            for (let i = 0; i < lines.length; i++) lines[i].end = i + 1 < lines.length ? lines[i + 1].start : text.length;
            const limit = items[index].maxNumberOfLines ?? 0;
            let width = -1, hashWidth = -1;
            if (items[index].measureNumericWidth) {
                if (textNodes.length) {
                    range.setStart(textNodes[0].node, 0);
                    const last = textNodes[textNodes.length - 1].node;
                    range.setEnd(last, last.textContent.length);
                    width = range.getBoundingClientRect().width * 15;
                } else width = 0;
                const hash = document.createElement('span');
                hash.style.cssText = 'position:absolute;white-space:pre;';
                hash.textContent = '#';
                node.append(hash);
                hashWidth = hash.getBoundingClientRect().width * 15;
                hash.remove();
            }
            // Native report geometry is in whole twips. Browser line boxes are
            // quantized to 1/64 px: ceiling 30.40625px adds a spurious twip to a
            // 456-twip two-line Arial field on every row. Round native boxes to
            // their nearest twip; retain conservative bounds for designer text.
            const native = node.style.getPropertyValue('--fx-report-font-points') !== '';
            const height = native ? Math.round(bounds.height * 15) : Math.ceil(bounds.height * 15);
            return { height, lines: limit > 0 ? lines.slice(0, limit) : lines, width, hashWidth };
        });
    } finally {
        host.remove();
    }
}
