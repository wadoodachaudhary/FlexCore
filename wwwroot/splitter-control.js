export function measureSplitter(rootEl, vertical) {
    if (!rootEl) return 0;
    const style = rootEl.ownerDocument.defaultView.getComputedStyle(rootEl);
    return (vertical ? rootEl.clientWidth : rootEl.clientHeight)
        - (parseFloat(vertical ? style.paddingLeft : style.paddingTop) || 0)
        - (parseFloat(vertical ? style.paddingRight : style.paddingBottom) || 0);
}

const splitterPreviews = new WeakMap();

export function disableSplitterPreview(root) {
    splitterPreviews.get(root)?.dispose();
    splitterPreviews.delete(root);
}

// Drag only a guide. Existing pane/table DOM stays untouched until pointerup;
// no pointer-move event is dispatched to Blazor's document listener.
export function enableSplitterPreview(root, dotNetRef, options, revision) {
    if (!root || !dotNetRef) return;
    disableSplitterPreview(root);
    const bar = root.querySelector(':scope > .fx-splitter-bar');
    const primary = root.querySelector(':scope > .fx-splitter-primary');
    if (!bar || !primary) return;
    const doc = root.ownerDocument;
    const win = doc.defaultView;
    let drag = null, generation = 0, disposed = false;

    const resolve = (raw, fallback, container) => {
        if (raw == null || String(raw).trim() === '') return fallback;
        const match = String(raw).trim().match(/^(-?\d+(?:\.\d+)?)\s*(%|px)?$/i);
        if (!match) return fallback;
        const value = Number(match[1]), percent = match[2] === '%';
        if (percent === options.proportional) return value;
        return percent ? value * container / 100 : value / container * 100;
    };

    const paint = () => {
        if (!drag) return;
        drag.frame = 0;
        const position = drag.barPosition + (drag.next - drag.startSize) * drag.unitPx;
        drag.line.style.transform = options.vertical
            ? `translateX(${position}px)` : `translateY(${position}px)`;
    };
    const update = event => {
        const coordinate = options.vertical ? event.clientX : event.clientY;
        drag.next = Math.max(drag.min, Math.min(drag.max,
            drag.startSize + (coordinate - drag.startPointer) / drag.unitPx));
        if (!drag.frame) drag.frame = win.requestAnimationFrame(paint);
    };
    const stop = event => { event.preventDefault(); event.stopPropagation(); };
    const onMove = event => {
        if (!drag || event.pointerId !== drag.pointerId) return;
        stop(event);
        if (event.pointerType === 'mouse' && !(event.buttons & 1)) { finish(false); return; }
        update(event);
    };
    const onUp = event => {
        if (!drag || event.pointerId !== drag.pointerId) return;
        stop(event);
        update(event); // Include the final position even if its animation frame has not run.
        finish(true);
    };
    const onCancel = event => {
        if (drag && (!event.pointerId || event.pointerId === drag.pointerId)) finish(false);
    };
    const onKey = event => {
        if (event.key === 'Escape' && drag) { stop(event); finish(false); }
    };
    const onVisibility = () => { if (doc.hidden) finish(false); };
    const cancel = () => finish(false);
    const finish = commit => {
        if (!drag) return;
        const current = drag;
        drag = null;
        win.cancelAnimationFrame(current.frame);
        current.observer.disconnect();
        current.overlay.remove();
        delete root.dataset.fxSplitterDragging;
        doc.removeEventListener('pointermove', onMove, true);
        doc.removeEventListener('pointerup', onUp, true);
        doc.removeEventListener('pointercancel', onCancel, true);
        doc.removeEventListener('keydown', onKey, true);
        doc.removeEventListener('visibilitychange', onVisibility);
        win.removeEventListener('blur', cancel);
        win.removeEventListener('resize', cancel);
        win.removeEventListener('scroll', cancel, true);
        try { bar.releasePointerCapture(current.pointerId); } catch { }
        if (!commit || !root.isConnected || Math.abs(current.next - current.startSize) < 0.01) return;

        const oldStyle = primary.getAttribute('style');
        const basis = `${current.next}${options.proportional ? '%' : 'px'}`;
        if (options.shrinkPrimaryToContent)
            primary.style[options.vertical ? 'maxWidth' : 'maxHeight'] = basis;
        else primary.style.flexBasis = basis;
        const committedStyle = primary.getAttribute('style');
        const request = generation;
        const rollback = () => {
            if (!disposed && request === generation && root.isConnected
                && primary.getAttribute('style') === committedStyle) {
                if (oldStyle == null) primary.removeAttribute('style');
                else primary.setAttribute('style', oldStyle);
            }
        };
        try {
            Promise.resolve(dotNetRef.invokeMethodAsync('CommitSplitterResizeAsync', current.next, current.container, revision))
                .then(accepted => { if (accepted === false) rollback(); }, rollback);
        } catch { rollback(); }
    };

    const onDown = event => {
        if (options.disabled || event.button !== 0 || !event.isPrimary) return;
        stop(event);
        if (drag) finish(false);
        generation++;
        const rect = root.getBoundingClientRect(), paneRect = primary.getBoundingClientRect();
        const barRect = bar.getBoundingClientRect();
        const vertical = options.vertical;
        const scale = (vertical ? rect.width / root.offsetWidth : rect.height / root.offsetHeight) || 1;
        const container = measureSplitter(root, vertical);
        if (container <= 0) return;
        const unitPx = scale * (options.proportional ? container / 100 : 1);
        const startSize = (vertical ? paneRect.width : paneRect.height) / unitPx;
        const min = resolve(options.minPrimary, options.proportional ? 0 : Math.max(0, options.minPrimarySize), container);
        let max = resolve(options.maxPrimary, options.proportional ? 100 : Math.max(min, options.maxPrimarySize), container);
        const floor = resolve(options.minSecondary, NaN, container);
        if (Number.isFinite(floor)) max = Math.min(max,
            (options.proportional ? 100 : container) - floor - (vertical ? barRect.width : barRect.height) / unitPx);
        if (options.shrinkPrimaryToContent) {
            const contentSize = vertical ? primary.scrollWidth : primary.scrollHeight;
            if (contentSize > 0) max = Math.min(max, options.proportional ? contentSize / container * 100 : contentSize);
        }
        max = Math.max(min, max);

        const overlay = doc.createElement('div'), line = doc.createElement('div');
        overlay.className = `fx-splitter-preview${vertical ? '' : ' fx-splitter-preview-horizontal'}`;
        line.className = 'fx-splitter-guide';
        // The temporary body overlay must retain the component's scoped CSS as well
        // as working in hosts that load only fx-shared.css.
        for (const name of root.getAttributeNames().filter(name => name.startsWith('b-'))) {
            overlay.setAttribute(name, ''); line.setAttribute(name, '');
        }
        overlay.setAttribute('aria-hidden', 'true');
        if (vertical) {
            line.style.top = `${rect.top}px`; line.style.height = `${rect.height}px`;
        } else {
            line.style.left = `${rect.left}px`; line.style.width = `${rect.width}px`;
        }
        overlay.appendChild(line); doc.body.appendChild(overlay);
        const observer = new MutationObserver(() => { if (!root.isConnected) finish(false); });
        drag = { overlay, line, observer, frame: 0, min, max, unitPx, container,
            startSize, next: startSize, pointerId: event.pointerId,
            startPointer: vertical ? event.clientX : event.clientY,
            barPosition: vertical ? barRect.left + barRect.width / 2 : barRect.top + barRect.height / 2 };
        root.dataset.fxSplitterDragging = '1';
        paint();
        observer.observe(doc.documentElement, { childList: true, subtree: true });
        doc.addEventListener('pointermove', onMove, true);
        doc.addEventListener('pointerup', onUp, true);
        doc.addEventListener('pointercancel', onCancel, true);
        doc.addEventListener('keydown', onKey, true);
        doc.addEventListener('visibilitychange', onVisibility);
        win.addEventListener('blur', cancel);
        win.addEventListener('resize', cancel);
        win.addEventListener('scroll', cancel, true);
        try { bar.setPointerCapture(event.pointerId); } catch { }
    };
    const stopMouse = event => { if (!options.disabled && event.button === 0) stop(event); };
    bar.addEventListener('pointerdown', onDown);
    bar.addEventListener('lostpointercapture', onCancel);
    bar.addEventListener('mousedown', stopMouse);
    splitterPreviews.set(root, { dispose: () => {
        disposed = true; generation++; finish(false);
        bar.removeEventListener('pointerdown', onDown);
        bar.removeEventListener('lostpointercapture', onCancel);
        bar.removeEventListener('mousedown', stopMouse);
    } });
}
