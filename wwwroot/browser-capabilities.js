// DOM capabilities shared by FlexCore controls. Imported only when requested.
const bindings = new WeakMap();
const stop = element => { bindings.get(element)?.(); bindings.delete(element); };
// Popup hosts with a live binding. A host the page removed while its popup was open never had its binding
// disposed (the element can no longer be passed in): it is let go when the next popup attaches.
const popupHosts = new Set();
const tabViewport = element => element.querySelector('.fx-tabstrip-viewport') || element;
const revealTab = element => {
    const view = tabViewport(element);
    const tab = view.querySelector('[role=tab][aria-selected=true], .fx-mdi-tab.active, .fx-tab-button.active');
    if (!tab) return;
    // Offset coordinates stay in layout pixels, including inside a zoomed report viewer.
    const left = tab.offsetLeft, right = left + tab.offsetWidth;
    if (left < view.scrollLeft) view.scrollLeft = left;
    else if (right > view.scrollLeft + view.clientWidth) view.scrollLeft = right - view.clientWidth;
    const top = tab.offsetTop, bottom = top + tab.offsetHeight;
    if (top < view.scrollTop) view.scrollTop = top;
    else if (bottom > view.scrollTop + view.clientHeight) view.scrollTop = bottom - view.clientHeight;
};
const tabLabelMeasurements = new WeakMap();
const fitTabLabels = element => {
    for (const label of element.querySelectorAll('.fx-tabstrip-text, .fx-mdi-tab-title')) {
        const style = getComputedStyle(label.parentElement), base = parseFloat(style.fontSize);
        const key = [label.textContent, label.clientWidth, base, style.fontWeight].join('/');
        if (!label.clientWidth || tabLabelMeasurements.get(label) === key) continue;
        // The two-line CSS clamp supplies the ellipsis after the maximum 30% reduction.
        for (const scale of [1, .9, .8, .7]) {
            label.style.fontSize = (base * scale) + 'px';
            if (label.scrollHeight <= label.clientHeight + 1) break;
        }
        tabLabelMeasurements.set(label, [label.textContent, label.clientWidth, base, style.fontWeight].join('/'));
    }
};
export async function invoke(action, element, data, callback) {
    if (action === 'dispose') { stop(element); return; }
    if (action === 'tabStripReveal') { fitTabLabels(element); revealTab(element); bindings.get(element)?.update?.(); return; }
    if (action === 'tabStripScroll') {
        const view = tabViewport(element);
        if (element.dataset.tabOrientation === 'Horizontal') view.scrollLeft += data * Math.max(24, view.clientWidth * 0.75);
        else view.scrollTop += data * Math.max(24, view.clientHeight * 0.75);
        return;
    }
    if (action === 'tabStripObserve') {
        stop(element);
        const view = tabViewport(element);
        if (!view) return false;
        let previous = '';
        const update = () => {
            if (!callback) return;
            const horizontal = element.dataset.tabOrientation === 'Horizontal';
            const style = getComputedStyle(element);
            // Exclude our own navigation buttons when deciding whether they are still needed.
            const availableHeight = element.dataset.tabOrientation === 'Vertical'
                ? element.clientHeight - parseFloat(style.paddingTop) - parseFloat(style.paddingBottom)
                : view.clientHeight;
            const overflow = horizontal ? view.scrollWidth > element.clientWidth + 1 : view.scrollHeight > availableHeight + 1;
            const offset = horizontal ? view.scrollLeft : view.scrollTop;
            const length = horizontal ? view.scrollWidth - view.clientWidth : view.scrollHeight - view.clientHeight;
            const left = overflow && offset > 1;
            const right = overflow && offset < length - 1;
            const state = `${overflow}/${left}/${right}`;
            if (state === previous) return;
            previous = state;
            callback.invokeMethodAsync('SetTabScrollState', overflow, left, right).catch(() => {});
        };
        const resize = new ResizeObserver(() => { fitTabLabels(element); revealTab(element); update(); });
        resize.observe(element); resize.observe(view);
        // Labels, close buttons and item removal can change the content width without resizing the viewport.
        const mutation = new MutationObserver(() => { fitTabLabels(element); update(); });
        mutation.observe(view, { childList: true, subtree: true, characterData: true });
        const key = e => {
            if (e.target.getAttribute('role') === 'tab' && ['ArrowLeft','ArrowRight','ArrowUp','ArrowDown','Home','End',' '].includes(e.key)) e.preventDefault();
        };
        view.addEventListener('scroll', update); view.addEventListener('keydown', key);
        if (callback) element.setAttribute('data-fx-overflow-ready', '');
        const cleanup = () => { resize.disconnect(); mutation.disconnect(); view.removeEventListener('scroll', update); view.removeEventListener('keydown', key); element.removeAttribute('data-fx-overflow-ready'); };
        cleanup.update = update;
        bindings.set(element, cleanup);
        fitTabLabels(element); revealTab(element); update();
        return true;
    }
    if (action === 'treeKeyboard') {
        stop(element);
        const key = e => {
            if (e.target === element && (['ArrowUp','ArrowDown','ArrowLeft','ArrowRight','Home','End',' ','F2'].includes(e.key) || ((e.ctrlKey || e.metaKey) && e.key === 'a'))) e.preventDefault();
        };
        const drag = e => {
            const node=e.target.closest('[data-node-id][draggable="true"]');
            if(node&&element.contains(node)&&e.dataTransfer){e.dataTransfer.setData('text/plain',node.dataset.nodeId);e.dataTransfer.effectAllowed='move';}
        };
        element.addEventListener('keydown',key);element.addEventListener('dragstart',drag);
        bindings.set(element,()=>{element.removeEventListener('keydown',key);element.removeEventListener('dragstart',drag);});return;
    }
    if (action === 'treeReveal') {
        const node=[...element.querySelectorAll('[data-node-id]')].find(n=>n.dataset.nodeId===data.id);
        if(node){const rect=node.getBoundingClientRect(),view=element.getBoundingClientRect();if(rect.top<view.top)element.scrollTop+=rect.top-view.top;else if(rect.bottom>view.bottom)element.scrollTop+=rect.bottom-view.bottom;}
        else if(data.virtualized&&data.index>=0)element.scrollTop=data.index*data.itemSize;
        return;
    }
    if (action === 'clipboardRead') return await navigator.clipboard.readText();
    if (action === 'clipboardWrite') { await navigator.clipboard.writeText(data); return; }
    if (action === 'download') {
        const bytes = data.stream ? await data.stream.arrayBuffer() : data.bytes;
        const url = URL.createObjectURL(new Blob([bytes], { type: data.type || 'application/octet-stream' }));
        const link = document.createElement('a'); link.href = url; link.download = data.name; link.click();
        setTimeout(() => URL.revokeObjectURL(url), 60000); return;
    }
    if (action === 'popup') {
        for (const host of popupHosts) if (!host.isConnected) { stop(host); popupHosts.delete(host); }
        stop(element);
        const panel = element.querySelector('[data-fx-popup]');
        const anchor = (data.anchorId && document.getElementById(data.anchorId)) || element.querySelector('[data-fx-anchor]');
        if (!panel || !anchor) return false;
        const position = () => {
            const a = anchor.getBoundingClientRect(), p = panel.getBoundingClientRect(), gap = data.offset || 0;
            let x = a.left, y = a.bottom + gap;
            if (data.placement.startsWith('Top')) y = a.top - p.height - gap;
            if (data.placement.startsWith('Left')) { x = a.left - p.width - gap; y = a.top; }
            if (data.placement.startsWith('Right')) { x = a.right + gap; y = a.top; }
            if (data.placement.endsWith('End')) x = a.right - p.width;
            if (y + p.height > innerHeight - 8 && a.top > p.height + gap) y = a.top - p.height - gap;
            panel.style.position = 'fixed'; panel.style.left = `${Math.max(8, Math.min(x, innerWidth-p.width-8))}px`;
            panel.style.top = `${Math.max(8, Math.min(y, innerHeight-p.height-8))}px`;
        };
        const dismiss = e => { if (!panel.contains(e.target) && !anchor.contains(e.target)) callback.invokeMethodAsync('DismissAsync').catch(()=>{}); };
        // On the host, which outlives its panel: a panel rendered again (closed and reopened within one round trip)
        // keeps its Escape. A nested popup's own host listener sees its keys first.
        const key = e => {
            if (e.key !== 'Escape' || !(e.target instanceof Element)) return;
            const current = e.target.closest('[data-fx-popup]');
            if (!current || current.parentElement !== element) return;
            e.stopPropagation(); callback.invokeMethodAsync('DismissAsync').catch(()=>{});
        };
        position(); const resize = new ResizeObserver(position); resize.observe(panel); resize.observe(anchor);
        window.addEventListener('resize',position); window.addEventListener('scroll',position,true);
        if (data.dismiss) document.addEventListener('pointerdown',dismiss,true);
        element.addEventListener('keydown',key);
        if (data.focus) (panel.querySelector('input,button,textarea,[tabindex="0"]') || panel).focus({preventScroll:true});
        popupHosts.add(element);
        bindings.set(element,()=>{ popupHosts.delete(element); resize.disconnect(); window.removeEventListener('resize',position); window.removeEventListener('scroll',position,true); document.removeEventListener('pointerdown',dismiss,true); element.removeEventListener('keydown',key); if(panel.contains(document.activeElement)) (anchor.querySelector('button,input,[tabindex]') || anchor).focus({preventScroll:true}); });
        return true;
    }
    if (action === 'mediaQuery') {
        stop(element); const query = matchMedia(data), changed = () => callback.invokeMethodAsync('SetMatchAsync',query.matches).catch(()=>{});
        query.addEventListener('change',changed); bindings.set(element,()=>query.removeEventListener('change',changed)); return query.matches;
    }
    if (action === 'dropZone') {
        stop(element); const drop = e => {
            e.preventDefault(); element.classList.remove('fx-drag-over');
            const input = document.getElementById(data.targetId)?.querySelector('[data-fx-upload-active="true"] input[type=file]');
            if (!input || input.disabled) return;
            input.files = e.dataTransfer.files; input.dispatchEvent(new Event('change',{bubbles:true}));
        };
        const over = e=>{e.preventDefault();element.classList.add('fx-drag-over');};
        const leave=()=>element.classList.remove('fx-drag-over');
        element.addEventListener('drop',drop); element.addEventListener('dragover',over);element.addEventListener('dragleave',leave);
        bindings.set(element,()=>{element.removeEventListener('drop',drop);element.removeEventListener('dragover',over);element.removeEventListener('dragleave',leave);}); return true;
    }
    if (action === 'signature') {
        stop(element); let points=null, path=null;
        const point=e=>{const r=element.getBoundingClientRect();return {x:(e.clientX-r.left)*data.width/r.width,y:(e.clientY-r.top)*data.height/r.height};};
        const down=e=>{if(e.button!==0)return;e.preventDefault();element.setPointerCapture(e.pointerId);points=[point(e)];path=document.createElementNS('http://www.w3.org/2000/svg','polyline');path.setAttribute('fill','none');path.setAttribute('stroke',data.color);path.setAttribute('stroke-width',data.strokeWidth);path.setAttribute('stroke-linecap','round');element.append(path);};
        const move=e=>{if(!points)return;for(const p of (e.getCoalescedEvents?.()||[e]))points.push(point(p));path.setAttribute('points',points.map(p=>`${p.x},${p.y}`).join(' '));};
        const up=e=>{if(!points)return;move(e);const finished=points;points=null;path.remove();path=null;callback.invokeMethodAsync('AddStrokeAsync',finished).catch(()=>{});};
        const cancel=()=>{points=null;path?.remove();path=null;};
        element.addEventListener('pointerdown',down);element.addEventListener('pointermove',move);element.addEventListener('pointerup',up);element.addEventListener('pointercancel',cancel);
        bindings.set(element,()=>{cancel();element.removeEventListener('pointerdown',down);element.removeEventListener('pointermove',move);element.removeEventListener('pointerup',up);element.removeEventListener('pointercancel',cancel);});return true;
    }
    if (action === 'speechStop') { bindings.get(element)?.stop?.(); return; }
    if (action === 'speechSupported') return !!(window.SpeechRecognition || window.webkitSpeechRecognition);
    if (action === 'speechStart') {
        stop(element); const Constructor=window.SpeechRecognition||window.webkitSpeechRecognition;
        if(!Constructor) throw new Error('Speech recognition is unavailable in this browser.');
        const recognition=new Constructor();recognition.lang=data.language;recognition.continuous=data.continuous;recognition.interimResults=true;
        recognition.onresult=e=>{let final='',interim='';for(let i=e.resultIndex;i<e.results.length;i++){if(e.results[i].isFinal)final+=e.results[i][0].transcript;else interim+=e.results[i][0].transcript;}callback.invokeMethodAsync('ReceiveAsync',final,interim).catch(()=>{});};
        recognition.onerror=e=>callback.invokeMethodAsync('SpeechErrorAsync',e.error).catch(()=>{});
        recognition.onend=()=>callback.invokeMethodAsync('SpeechEndedAsync').catch(()=>{});
        const cleanup=()=>{recognition.onend=null;recognition.onresult=null;recognition.onerror=null;recognition.abort();};cleanup.stop=()=>recognition.stop();bindings.set(element,cleanup);recognition.start();return;
    }
    if (action === 'media') {
        if(data.command==='play')await element.play();
        else if(data.command==='pause')element.pause();
        else if(data.command==='seek')element.currentTime=data.value;
        else if(data.command==='volume')element.volume=data.value;
        else if(data.command==='rate')element.playbackRate=data.value;
        else if(data.command==='fullscreen')await element.requestFullscreen();
        return {time:element.currentTime,duration:Number.isFinite(element.duration)?element.duration:0,paused:element.paused};
    }
    // MDI split view (MdiHost): a press anywhere in the pane of the window that is not the active one (the pane carries
    // data-fx-mdi-activate) makes that window the active one, with any mouse button and on any control. A control may keep
    // its press to itself (a splitter bar, a column grip or a dialog title bar stops or cancels it, so neither a mousedown
    // nor a focusin reaches the pane): this listens in the capture phase, ahead of every control, and never stops or
    // cancels the press. A mousedown that came without a pointerdown (assistive technology, a script) is a press too.
    // A mouse press settles once its mousedown has moved the focus: at its release, before the page sees its mouseup or
    // click, or one task later, whichever comes first. A touch or pen press settles after it lifts, once the mousedown of a
    // tap has moved the focus; one the browser cancels (a pan) does nothing. A keyboard the press left in another window, or
    // outside every window, goes to the control the pressed window focused last that is still there, as an activated MDI
    // child gets its active control back; with none, or after a touch or pen (no on-screen keyboard opens), it only leaves
    // that other window. Then the window is reported once, unless a focusin reached the pane during the press (the pane's
    // own focusin handler reported it) or a report of this watch for the pane is still on its way. In the active window a
    // press takes back a keyboard left in the other pane, unless that pane's activation is still on its way.
    if (action === 'mdiPanes') {
        stop(element);
        if (!callback) return false;
        const doc = element.ownerDocument, partner = 'data-fx-mdi-activate';
        const recent = new WeakMap(), sentAt = new WeakMap(), focusedAt = new WeakMap(), open = new Set();
        let live = true, pointerPressed = false;
        // The pane a node is in: the child of this panes element it descends from, so a nested host's panes stay its own.
        const paneOf = node => {
            for (let n = node instanceof Element ? node : null; n && n !== element; n = n.parentElement)
                if (n.parentElement === element) return n.classList.contains('fx-mdi-pane') ? n : null;
            return null;
        };
        // An activation for a pane, sent by this watch (sentAt) or by the pane's focusin handler (focusedAt), is on its way
        // while the pane is still the partner, for at most 2 s (a message a dead link swallowed holds nothing back for longer).
        // The server's answer takes the attribute away.
        const onItsWay = (times, pane) => pane.hasAttribute(partner) && performance.now() - (times.get(pane) ?? -Infinity) < 2000;
        const answered = new MutationObserver(records => {
            for (const r of records) if (!r.target.hasAttribute(partner)) { sentAt.delete(r.target); focusedAt.delete(r.target); }
        });
        answered.observe(element, { subtree: true, attributes: true, attributeFilter: [partner] });
        // The last four controls each pane focused, newest first. Neither a splitter bar (it would take the arrows, Home and
        // End) nor a toolbar button (a desktop toolbar's buttons take no focus) is one the keyboard goes back to; another
        // control placed on a toolbar (a check box, a text box) is.
        const remember = e => {
            const pane = paneOf(e.target);
            if (!pane || e.target === pane || e.target.closest('[role=separator]')
                || e.target.matches(':is([role=toolbar], .fx-toolbar) :is(button, [role=button])')) return;
            const list = [new WeakRef(e.target)];
            for (const ref of recent.get(pane) || []) {
                const node = ref.deref();
                if (node && node !== e.target && node.isConnected && list.length < 4) list.push(ref);
            }
            recent.set(pane, list);
        };
        // A focusin that bubbles up here reaches the pane's focusin handler too, which reports the window.
        const reached = e => {
            const pane = paneOf(e.target);
            if (pane?.hasAttribute(partner)) focusedAt.set(pane, performance.now());
            for (const press of open) if (press.pane.contains(e.target)) press.carried = true;
        };
        const usable = node => !!node?.isConnected && node.getClientRects().length > 0
            && !node.matches(':disabled') && !node.closest('[inert]');
        const restore = (pane, before) => {
            for (const ref of recent.get(pane) || []) {
                const node = ref.deref();
                if (!usable(node) || !pane.contains(node)) continue;
                node.focus({ preventScroll: true, focusVisible: false });
                if (doc.activeElement !== before) return;
            }
            before.blur();
        };
        const settle = press => {
            if (press.done) return;
            press.done = true;
            const { pane, before } = press;
            if (live && pane.isConnected && before && before !== doc.body && before !== doc.documentElement
                && doc.activeElement === before && !pane.contains(before)) {
                if (press.coarse) before.blur();
                else restore(pane, before);   // its focusin, while the press is open, counts as reaching the pane
            }
            open.delete(press);
            const id = pane.getAttribute(partner);
            if (!live || !pane.isConnected || id === null || press.carried || onItsWay(sentAt, pane)) return;
            sentAt.set(pane, performance.now());
            try { callback.invokeMethodAsync('ActivatePartnerWindowAsync', id).catch(() => {}); } catch { }
        };
        const down = e => {
            const pane = paneOf(e.target);
            if (!pane) return;
            const before = doc.activeElement, other = paneOf(before);
            if (!pane.hasAttribute(partner)
                && !(other && other !== pane && !onItsWay(sentAt, other) && !onItsWay(focusedAt, other))) return;
            const press = { pane, before, coarse: e.pointerType === 'touch' || e.pointerType === 'pen', pointer: e.pointerId, carried: false, done: false };
            open.add(press);
            if (!press.coarse) setTimeout(() => settle(press));
        };
        const pointer = e => { pointerPressed = true; setTimeout(() => { pointerPressed = false; }); down(e); };
        // The mousedown of a touch tap belongs to the touch's press.
        const mouse = e => {
            if (!pointerPressed && !e.sourceCapabilities?.firesTouchEvents && ![...open].some(p => p.coarse)) down(e);
            pointerPressed = false;
        };
        // A pointer lifts, or is cancelled, wherever it is then (also off the panes, when its control went away).
        const lifted = e => { for (const p of [...open]) if (!p.coarse) settle(p); else if (p.pointer === e.pointerId) setTimeout(() => settle(p)); };
        const cancelled = e => { for (const p of [...open]) if (p.coarse && p.pointer === e.pointerId) { p.done = true; open.delete(p); } };
        const release = () => { for (const p of [...open]) settle(p); };
        const listeners = [[element, 'pointerdown', pointer], [element, 'mousedown', mouse], [doc, 'pointerup', lifted],
            [doc, 'pointercancel', cancelled], ...['mouseup', 'click', 'contextmenu'].map(type => [element, type, release])];
        for (const [target, type, listener] of listeners) target.addEventListener(type, listener, { capture: true, passive: true });
        element.addEventListener('focusin', remember, true);
        element.addEventListener('focusin', reached);
        bindings.set(element, () => {
            live = false; open.clear(); answered.disconnect();
            for (const [target, type, listener] of listeners) target.removeEventListener(type, listener, true);
            element.removeEventListener('focusin', remember, true);
            element.removeEventListener('focusin', reached);
        });
        return true;
    }
    throw new Error(`Unknown FlexCore browser capability: ${action}`);
}
