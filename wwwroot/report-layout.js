export function capturePointer(element, pointerId) {
    if (element?.isConnected) element.setPointerCapture(pointerId);
}

// Font shaping/wrapping belongs to the browser; C# owns all band/page placement.
export async function measureText(items) {
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
        return nodes.map(node => Math.ceil(node.getBoundingClientRect().height * 15));
    } finally {
        host.remove();
    }
}
