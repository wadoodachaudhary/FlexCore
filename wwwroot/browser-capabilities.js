// DOM capabilities shared by FlexCore controls. Imported only when requested.
const bindings = new WeakMap();
const stop = element => { bindings.get(element)?.(); bindings.delete(element); };
export async function invoke(action, element, data, callback) {
    if (action === 'dispose') { stop(element); return; }
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
        const key = e => { if (e.key === 'Escape') { e.stopPropagation(); callback.invokeMethodAsync('DismissAsync').catch(()=>{}); } };
        position(); const resize = new ResizeObserver(position); resize.observe(panel); resize.observe(anchor);
        window.addEventListener('resize',position); window.addEventListener('scroll',position,true);
        if (data.dismiss) document.addEventListener('pointerdown',dismiss,true);
        panel.addEventListener('keydown',key);
        if (data.focus) (panel.querySelector('input,button,textarea,[tabindex="0"]') || panel).focus({preventScroll:true});
        bindings.set(element,()=>{ resize.disconnect(); window.removeEventListener('resize',position); window.removeEventListener('scroll',position,true); document.removeEventListener('pointerdown',dismiss,true); panel.removeEventListener('keydown',key); if(panel.contains(document.activeElement)) (anchor.querySelector('button,input,[tabindex]') || anchor).focus({preventScroll:true}); });
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
    throw new Error(`Unknown FlexCore browser capability: ${action}`);
}
