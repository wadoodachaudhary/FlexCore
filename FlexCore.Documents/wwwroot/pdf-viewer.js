// Browser-only PDF rendering, text selection, file transfer and print bridge.
// The Blazor control owns all toolbar and form controls. No document content is sent to a service.
const viewers = new WeakMap();
// A Blazor ElementReference can resolve to null after the renderer removes its DOM.
// Keep resource ownership separate from the element used for page rendering.
const sessions = new Map();
let enginePromise;
const engine = () => enginePromise ??= import('./vendor/pdfjs/build/pdf.min.mjs').then(pdfjs => {
    pdfjs.GlobalWorkerOptions.workerSrc = new URL('./vendor/pdfjs/build/pdf.worker.min.mjs', import.meta.url).href;
    return pdfjs;
});
const editor = () => import('./vendor/pdf-lib/pdf-lib.min.mjs');
const asset = path => new URL(`./vendor/pdfjs/${path}/`, import.meta.url).href;
const openPdf = async (bytes, password, state) => {
    const pdfjs = await engine();
    if (state.disposed) throw new Error('PDF loading was cancelled.');
    const task = pdfjs.getDocument({
        data: bytes.slice(), password, cMapUrl: asset('cmaps'), cMapPacked: true,
        standardFontDataUrl: asset('standard_fonts'), wasmUrl: asset('wasm'), iccUrl: asset('iccs'),
        isEvalSupported: false, enableXfa: false
    });
    state.loadingTask = task;
    return task.promise;
};
function clearPage(host) {
    const canvas = host?.querySelector('canvas');
    if (canvas) { canvas.width = 0; canvas.height = 0; }
    host?.querySelector('.fx-pdf-text')?.replaceChildren();
    host?.querySelector('.fx-pdf-search-layer')?.replaceChildren();
}
async function disposeViewer(state) {
    if (!state) return;
    if (state.disposal) return state.disposal;
    state.disposed = true; state.renderGeneration++;
    if (viewers.get(state.host) === state) viewers.delete(state.host);
    if (state.sessionId && sessions.get(state.sessionId) === state) sessions.delete(state.sessionId);
    state.abort.abort();
    state.disposal = (async () => {
        // Cancellation can race worker/text-layer completion. Always release the
        // workers even if a render already failed, and make repeated disposal safe.
        try { await cancelRender(state); }
        catch { /* The failed render is reported by its caller, not again during teardown. */ }
        finally {
            const tasks = new Set([state.loadingTask, state.pdf?.loadingTask].filter(Boolean));
            await Promise.allSettled([...tasks].map(task => task.destroy()));
        }
    })();
    return state.disposal;
}
async function cancelRender(state) {
    state.renderTask?.cancel();
    try { await state.renderTask?.promise; } catch (error) { if (error.name !== 'RenderingCancelledException') throw error; }
    state.renderTask = null;
    state.textLayer?.cancel();
}
async function readText(page) {
    // ReadableStream async iteration is absent in some WebKit releases. The
    // standard reader API works there and preserves PDF.js's full text payload.
    const reader = page.streamTextContent().getReader();
    const content = { items: [], styles: Object.create(null), lang: null };
    try {
        while (true) {
            const { value, done } = await reader.read();
            if (done) return content;
            content.items.push(...value.items);
            Object.assign(content.styles, value.styles);
            content.lang ??= value.lang;
        }
    } finally { reader.releaseLock(); }
}
async function metadata(state) {
    const objects = await state.pdf.getFieldObjects() ?? {};
    return { pages: state.pdf.numPages, bookmarks: await bookmarks(state.pdf), fields: (objects instanceof Map ? [...objects.entries()] : Object.entries(objects)).map(([name, entries]) => {
        const field = entries.find(e => e.type) ?? entries[0];
        const type = field.type;
        return { name, type, textValue: Array.isArray(field.value) ? field.value[0] ?? '' : String(field.value ?? ''),
            boolValue: field.value !== 'Off' && field.value !== false && !!field.value,
            options: (field.type === 'radiobutton' ? entries.map(e => e.exportValues).filter(Boolean) : field.items ?? field.options ?? []).map(o => typeof o === 'string' ? o : o.exportValue ?? o.displayValue),
            readOnly: field.editable === false || !!field.readOnly || !!field.multipleSelection || !['text','checkbox','combobox','listbox','radiobutton'].includes(type) };
    }) };
}
// Only resolve internal outline destinations. Document-provided URLs and actions are never executed.
async function bookmarks(pdf) {
    const resolve = async items => Promise.all(items.map(async item => {
        let page = null;
        try {
            const dest = typeof item.dest === 'string' ? await pdf.getDestination(item.dest) : item.dest;
            if (Array.isArray(dest) && dest.length) {
                const index = Number.isInteger(dest[0]) ? dest[0] : await pdf.getPageIndex(dest[0]);
                if (index >= 0 && index < pdf.numPages) page = index + 1;
            }
        } catch { /* Preserve the outline label even when its destination is malformed. */ }
        return { title: item.title ?? '', page, children: await resolve(item.items ?? []) };
    }));
    return resolve(await pdf.getOutline() ?? []);
}
// Keep one offset map for searching and highlighting, including text split by font/style changes.
function textMap(content) {
    let text = '';
    const segments = [];
    const items = content.items.filter(item => typeof item.str === 'string');
    for (let i = 0; i < items.length; i++) {
        const item = items[i];
        segments.push({ start: text.length, length: item.str.length });
        text += item.str;
        const next = items[i + 1];
        if (item.hasEOL) text += ' ';
        else if (next && !/\s$/.test(item.str) && !/^\s/.test(next.str)) {
            const gap = next.transform[4] - item.transform[4] - item.width;
            if (Math.abs(next.transform[5] - item.transform[5]) > 2 || gap > Math.abs(item.transform[0]) * .15) text += ' ';
        }
    }
    return { text, segments };
}
function showSearch(host, state, content, data) {
    const overlay = host.querySelector('.fx-pdf-search-layer');
    overlay.replaceChildren();
    const holderRect = host.querySelector('.fx-pdf-page').getBoundingClientRect();
    const { segments } = textMap(content);
    let activeElement;
    for (const match of data.matches ?? []) {
        const active = data.activeMatch?.page === match.page && data.activeMatch?.index === match.index;
        for (let i = 0; i < segments.length; i++) {
            const segment = segments[i];
            const start = Math.max(match.index, segment.start) - segment.start;
            const end = Math.min(match.index + match.length, segment.start + segment.length) - segment.start;
            const node = state.textLayer.textDivs[i]?.firstChild;
            if (end <= start || !node || node.nodeType !== Node.TEXT_NODE) continue;
            const range = document.createRange(); range.setStart(node, start); range.setEnd(node, end);
            for (const rect of range.getClientRects()) {
                const mark = document.createElement('span');
                mark.className = active ? 'fx-pdf-match fx-pdf-active-match' : 'fx-pdf-match';
                mark.style.cssText = `left:${rect.left-holderRect.left}px;top:${rect.top-holderRect.top}px;width:${rect.width}px;height:${rect.height}px;`;
                overlay.append(mark);
                if (active && !activeElement) activeElement = mark;
            }
        }
    }
    // Scroll only the viewer, without moving the host page or stealing keyboard focus.
    if (activeElement) {
        const viewport = host.querySelector('.fx-pdf-viewport');
        const rect = activeElement.getBoundingClientRect(), view = viewport.getBoundingClientRect();
        viewport.scrollTop += rect.top - view.top - viewport.clientHeight / 2;
        viewport.scrollLeft += rect.left - view.left - viewport.clientWidth / 2;
    }
}
async function editedBytes(state, data) {
    const fields = data.fields ?? [], notes = data.annotations ?? [];
    if (!fields.some(f => f.dirty) && !notes.length) return state.original.slice();
    const lib = await editor();
    const doc = await lib.PDFDocument.load(state.original);
    const form = doc.getForm();
    if (form.hasXFA()) throw new Error('Editing XFA forms is not supported. The original PDF can still be downloaded.');
    for (const field of fields.filter(f => f.dirty)) {
        const target = form.getField(field.name);
        if (target.isReadOnly()) throw new Error(`Field '${field.name}' is read-only.`);
        if (target instanceof lib.PDFTextField) target.setText(field.textValue ?? '');
        else if (target instanceof lib.PDFCheckBox) field.boolValue ? target.check() : target.uncheck();
        else if (target instanceof lib.PDFDropdown || target instanceof lib.PDFOptionList || target instanceof lib.PDFRadioGroup) {
            field.textValue ? target.select(field.textValue) : target.clear();
        } else throw new Error(`Editing field '${field.name}' is not supported.`);
    }
    for (const note of notes) {
        const page = doc.getPage(note.page - 1);
        const x = note.x, y = note.y, w = Math.max(note.width, 12), h = Math.max(note.height, 12);
        const annotation = doc.context.obj({ Type: 'Annot', Subtype: note.kind === 'Highlight' ? 'Highlight' : 'Text',
            Rect: [x,y,x+w,y+h], Contents: lib.PDFHexString.fromText(note.text ?? ''),
            NM: lib.PDFHexString.fromText(note.id), C: note.kind === 'Highlight' ? [1,.85,0] : [1,.65,0],
            F: 4, ...(note.kind === 'Highlight' ? { QuadPoints: [x,y+h,x+w,y+h,x,y,x+w,y], CA: .35 } : { Name: 'Comment' }) });
        page.node.addAnnot(doc.context.register(annotation));
    }
    return doc.save();
}
async function render(host, state, data) {
    const generation = ++state.renderGeneration;
    const previous = state.queue ?? Promise.resolve();
    state.queue = previous.catch(() => {}).then(async () => {
        if (generation !== state.renderGeneration || viewers.get(host) !== state) return;
        await cancelRender(state);
        if (data.refresh) {
            const bytes = await editedBytes(state, data);
            const pdf = await openPdf(bytes, state.password, state);
            if (generation !== state.renderGeneration || viewers.get(host) !== state) { await pdf.loadingTask.destroy(); return; }
            await state.pdf.loadingTask.destroy(); state.pdf = pdf;
        }
        const page = await state.pdf.getPage(Math.max(1, Math.min(state.pdf.numPages, data.page)));
        if (generation !== state.renderGeneration || viewers.get(host) !== state) return;
        const viewport = page.getViewport({ scale: data.zoom, rotation: (page.rotate + (data.rotation ?? 0)) % 360 });
        const holder = host.querySelector('.fx-pdf-page');
        const canvas = host.querySelector('canvas');
        const text = host.querySelector('.fx-pdf-text');
        const ratio = Math.min(window.devicePixelRatio || 1, 2);
        canvas.width = Math.ceil(viewport.width * ratio); canvas.height = Math.ceil(viewport.height * ratio);
        canvas.style.width = holder.style.width = `${viewport.width}px`;
        canvas.style.height = holder.style.height = `${viewport.height}px`;
        holder.style.setProperty('--scale-factor', String(data.zoom));
        holder.style.setProperty('--total-scale-factor', String(data.zoom));
        text.replaceChildren();
        state.renderTask = page.render({ canvasContext: canvas.getContext('2d'), viewport, transform: [ratio,0,0,ratio,0,0] });
        await state.renderTask.promise;
        if (generation !== state.renderGeneration || viewers.get(host) !== state) return;
        const pdfjs = await engine();
        const content = await readText(page);
        if (generation !== state.renderGeneration || viewers.get(host) !== state) return;
        state.textLayer = new pdfjs.TextLayer({ textContentSource: content, container: text, viewport });
        // TextLayer coordinates are unrotated. Supply explicit dimensions because
        // its CSS round()/scale-round variables are not available in every browser.
        text.style.width = `${viewport.rawDims.pageWidth * data.zoom}px`;
        text.style.height = `${viewport.rawDims.pageHeight * data.zoom}px`;
        await state.textLayer.render();
        if (generation !== state.renderGeneration || viewers.get(host) !== state) return;
        if (state.page !== page.pageNumber) {
            const scroller = host.querySelector('.fx-pdf-viewport');
            scroller.scrollTop = scroller.scrollLeft = 0;
        }
        state.page = page.pageNumber; state.viewport = viewport;
        showSearch(host, state, content, data);
        const annotations = await page.getAnnotations();
        return annotations.filter(a => a.subtype === 'Text' || a.subtype === 'Highlight').map(a => ({
            id: a.id, text: a.contentsObj?.str ?? a.contents ?? '', kind: a.subtype, page: page.pageNumber,
            x: a.rect[0], y: a.rect[1], width: a.rect[2]-a.rect[0], height: a.rect[3]-a.rect[1]
        }));
    });
    return state.queue;
}
// One export keeps the DOM boundary explicit and scoped to this viewer instance.
export async function invoke(action, host, data = {}, sessionId = null) {
    if (action === 'dispose') {
        const old = sessions.get(sessionId) ?? viewers.get(host);
        await disposeViewer(old);
        const oldHost = old?.host ?? host;
        if (!viewers.has(oldHost)) clearPage(oldHost);
        return;
    }
    if (action === 'load') {
        if (!host?.isConnected) throw new Error('The PDF viewer is no longer mounted.');
        const old = sessions.get(sessionId) ?? viewers.get(host);
        const state = { host, sessionId, renderGeneration: 0, password: data.password, abort: new AbortController() };
        viewers.set(host, state);
        if (sessionId) sessions.set(sessionId, state);
        await disposeViewer(old);
        try {
            if (state.disposed || viewers.get(host) !== state) throw new Error('PDF loading was superseded.');
            clearPage(host);
            const bytes = data.bytes?.length ? new Uint8Array(data.bytes) : await fetch(data.url, { signal: state.abort.signal }).then(r => {
                if (!r.ok) throw new Error(`PDF request failed (${r.status}).`); return r.arrayBuffer();
            }).then(b => new Uint8Array(b));
            if (bytes.length > (data.maxFileSize ?? 50*1024*1024)) throw new Error('PDF exceeds MaxFileSize.');
            state.original = bytes.slice(); state.pdf = await openPdf(bytes, data.password, state);
            if (viewers.get(host) !== state || state.disposed) throw new Error('PDF loading was superseded.');
            return await metadata(state);
        } catch (error) {
            await disposeViewer(state);
            throw error;
        }
    }
    const state = viewers.get(host);
    if (!state?.pdf) throw new Error('Open a PDF first.');
    if (action === 'render') return render(host,state,data);
    if (action === 'search') {
        const matches = [], query = String(data.query ?? '');
        let payloadBytes = 0;
        if (!query) return { matches, isTruncated: false };
        const literal = query.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
        const expression = new RegExp(literal, data.matchCase ? 'gu' : 'giu');
        for (let page = 1; page <= state.pdf.numPages; page++) {
            const { text } = textMap(await readText(await state.pdf.getPage(page)));
            for (const match of text.matchAll(expression)) {
                // Bound interop payloads and announce truncation in the UI/API.
                const start = Math.max(0, match.index - 25);
                const item = { page, index: match.index, length: match[0].length,
                    text: text.slice(start, Math.min(start + 120, match.index + match[0].length + 40)) };
                payloadBytes += new TextEncoder().encode(JSON.stringify(item)).length;
                if (matches.length === 200 || payloadBytes > 24000) return { matches, isTruncated: true };
                matches.push(item);
            }
        }
        return { matches, isTruncated: false };
    }
    if (action === 'selection') {
        const selection = window.getSelection(); if (!selection?.rangeCount || !state.viewport) return [];
        const range = selection.getRangeAt(0); const layer = host.querySelector('.fx-pdf-text');
        if (!layer.contains(range.commonAncestorContainer)) return [];
        const rect = host.querySelector('.fx-pdf-page').getBoundingClientRect();
        const result = [...range.getClientRects()].filter(r => r.width > 0 && r.height > 0).map(r => {
            const a = state.viewport.convertToPdfPoint(r.left-rect.left,r.top-rect.top);
            const b = state.viewport.convertToPdfPoint(r.right-rect.left,r.bottom-rect.top);
            return { page: state.page, x: Math.min(a[0],b[0]), y: Math.min(a[1],b[1]), width: Math.abs(b[0]-a[0]), height: Math.abs(b[1]-a[1]), text: selection.toString() };
        });
        return result;
    }
    if (action === 'notePosition') {
        const page = await state.pdf.getPage(data.page); const box = page.view;
        return { page: data.page, x: box[0]+24, y: box[3]-48, width: 24, height: 24 };
    }
    if (action === 'saveStream') return editedBytes(state,data);
    if (action === 'download' || action === 'print') {
        const bytes = await editedBytes(state,data); const url = URL.createObjectURL(new Blob([bytes],{type:'application/pdf'}));
        if (action === 'download') {
            const link = document.createElement('a'); link.href=url; link.download=data.fileName || 'document.pdf'; link.click();
            setTimeout(() => URL.revokeObjectURL(url),60000);
        } else {
            const frame = document.createElement('iframe'); frame.style.cssText='position:fixed;width:1px;height:1px;left:-10000px;';
            frame.onload=()=>{frame.contentWindow.focus();frame.contentWindow.print();}; frame.src=url; document.body.append(frame);
            setTimeout(()=>{frame.remove();URL.revokeObjectURL(url);},120000);
        }
        return;
    }
    throw new Error(`Unknown PDF viewer action '${action}'.`);
}
