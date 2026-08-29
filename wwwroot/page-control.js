const pageNavigationBindings = new WeakMap();

const focusableSelector = [
    "button:not([disabled])",
    "input:not([disabled]):not([type='hidden'])",
    "select:not([disabled])",
    "textarea:not([disabled])",
    "a[href]",
    "[tabindex]:not([tabindex='-1'])"
].join(",");

const navigationOverlaySelector =
    ".fx-dropdown-panel, .fx-dropdown-backdrop, .fx-context-menu, [role='menu'], [role='listbox']";

function isVisible(element) {
    if (!(element instanceof HTMLElement) || element.hidden) return false;
    if (element.getClientRects().length === 0) return false;
    const style = element.ownerDocument.defaultView?.getComputedStyle(element);
    return style?.display !== "none" && style?.visibility !== "hidden";
}

function isFocusable(element) {
    if (!(element instanceof HTMLElement) || !element.matches(focusableSelector)) return false;
    if (element.matches("[disabled], [aria-disabled='true']")) return false;
    if (element.tabIndex < 0 || !isVisible(element)) return false;
    return !element.closest(navigationOverlaySelector);
}

function normalizeNodes(nodes) {
    if (!Array.isArray(nodes)) return [];

    return nodes
        .filter(node => node
            && typeof node.id === "string" && node.id.trim().length > 0
            && typeof node.selector === "string" && node.selector.trim().length > 0)
        .map(node => ({
            id: node.id,
            selector: node.selector,
            mode: ["element", "descendants"].includes(node.mode) ? node.mode : "auto",
            next: typeof node.next === "string" && node.next.length > 0 ? node.next : null,
            previous: typeof node.previous === "string" && node.previous.length > 0 ? node.previous : null,
            shortcuts: Array.isArray(node.shortcuts)
                ? node.shortcuts.filter(shortcut => shortcut && typeof shortcut.key === "string" && shortcut.key.length > 0)
                : []
        }));
}

function candidatesForRegion(region, mode) {
    if (mode === "element")
        return isFocusable(region) ? [region] : [];

    const descendants = () =>
        Array.from(region.querySelectorAll(focusableSelector)).filter(isFocusable);

    if (mode === "descendants")
        return descendants();

    return isFocusable(region) ? [region] : descendants();
}

function collectTargets(root, nodes) {
    const targets = [];
    const targetsByNode = new Map();
    const seen = new Set();

    for (const node of nodes) {
        const nodeTargets = [];
        targetsByNode.set(node.id, nodeTargets);

        let regions;
        try {
            regions = root.querySelectorAll(node.selector);
        } catch {
            continue;
        }

        for (const region of regions) {
            for (const element of candidatesForRegion(region, node.mode)) {
                if (seen.has(element)) continue;
                seen.add(element);

                const target = { node, element };
                nodeTargets.push(target);
                targets.push(target);
            }
        }
    }

    return {
        targets,
        targetsByNode,
        nodesById: new Map(nodes.map(node => [node.id, node]))
    };
}

function resolveCurrentIndex(targets, activeElement) {
    return targets.findIndex(target =>
        target.element === activeElement || target.element.contains(activeElement));
}

function isNodeBoundary(target, targetsByNode, direction) {
    const nodeTargets = targetsByNode.get(target.node.id) ?? [];
    if (nodeTargets.length === 0) return true;
    return direction > 0
        ? nodeTargets[nodeTargets.length - 1] === target
        : nodeTargets[0] === target;
}

function resolveLinkedTarget(currentTarget, direction, graph) {
    if (!isNodeBoundary(currentTarget, graph.targetsByNode, direction)) return null;

    const edgeName = direction > 0 ? "next" : "previous";
    let targetId = currentTarget.node[edgeName];
    const visited = new Set([currentTarget.node.id]);

    while (targetId && !visited.has(targetId)) {
        visited.add(targetId);
        const nodeTargets = graph.targetsByNode.get(targetId) ?? [];
        if (nodeTargets.length > 0)
            return direction > 0 ? nodeTargets[0] : nodeTargets[nodeTargets.length - 1];

        targetId = graph.nodesById.get(targetId)?.[edgeName] ?? null;
    }

    return null;
}

function resolveNaturalTarget(targets, currentIndex, direction, wrap) {
    let nextIndex = currentIndex < 0
        ? (direction > 0 ? 0 : targets.length - 1)
        : currentIndex + direction;

    if (nextIndex < 0 || nextIndex >= targets.length) {
        if (!wrap) return null;
        nextIndex = nextIndex < 0 ? targets.length - 1 : 0;
    }

    return targets[nextIndex] ?? null;
}

function isTextEditor(element) {
    return element instanceof Element
        && !!element.closest("input, textarea, select, [contenteditable='true']");
}

function shortcutMatches(event, shortcut) {
    return event.key.toLocaleLowerCase() === shortcut.key.toLocaleLowerCase()
        && event.altKey === !!shortcut.alt
        && event.ctrlKey === !!shortcut.control
        && event.metaKey === !!shortcut.meta
        && event.shiftKey === !!shortcut.shift;
}

function resolveShortcut(event, graph) {
    for (const node of graph.nodesById.values()) {
        for (const shortcut of node.shortcuts) {
            if (!shortcutMatches(event, shortcut)) continue;

            const hasCommandModifier = !!shortcut.alt || !!shortcut.control || !!shortcut.meta;
            if (isTextEditor(event.target) && !shortcut.allowInEditor && !hasCommandModifier)
                continue;

            const nodeTargets = graph.targetsByNode.get(node.id) ?? [];
            if (nodeTargets.length > 0)
                return { target: nodeTargets[0], action: shortcut.action };
        }
    }

    return null;
}

function focusTarget(target, action = "focus") {
    target.element.focus({ preventScroll: true });
    if (action === "activate")
        target.element.click();
}

export function registerPageNavigationGraph(root, nodeDefinitions, wrap = true) {
    if (!root) return;
    unregisterPageNavigation(root);

    const nodes = normalizeNodes(nodeDefinitions);
    const onKeyDown = event => {
        if (event.target instanceof Element
            && event.target.closest("[data-fx-page-control]") !== root) return;
        if (event.target instanceof Element
            && event.target.closest(navigationOverlaySelector)) return;

        const graph = collectTargets(root, nodes);
        if (graph.targets.length === 0) return;

        const shortcut = resolveShortcut(event, graph);
        if (shortcut) {
            event.preventDefault();
            event.stopPropagation();
            focusTarget(shortcut.target, shortcut.action);
            return;
        }

        if (event.key !== "Tab" || event.altKey || event.ctrlKey || event.metaKey) return;

        // GridControl owns Tab by default. A grid participates as one page-level
        // stop only when it explicitly delegates Tab to PageControl.
        const sourceGrid = event.target instanceof Element
            ? event.target.closest("[data-fx-grid-tab-navigation]")
            : null;
        if (sourceGrid
            && root.contains(sourceGrid)
            && sourceGrid.dataset.fxGridTabNavigation !== "page-control") return;

        const currentIndex = resolveCurrentIndex(graph.targets, root.ownerDocument.activeElement);
        const direction = event.shiftKey ? -1 : 1;
        const currentTarget = currentIndex >= 0 ? graph.targets[currentIndex] : null;
        const nextTarget = currentTarget
            ? resolveLinkedTarget(currentTarget, direction, graph)
                ?? resolveNaturalTarget(graph.targets, currentIndex, direction, wrap)
            : resolveNaturalTarget(graph.targets, currentIndex, direction, wrap);

        if (!nextTarget) return;

        event.preventDefault();
        event.stopPropagation();
        focusTarget(nextTarget);
    };

    root.addEventListener("keydown", onKeyDown, true);
    pageNavigationBindings.set(root, onKeyDown);
}

export function registerPageNavigation(root, selectors, wrap = true) {
    const nodes = Array.isArray(selectors)
        ? selectors.map((selector, index) => ({
            id: `region-${index}`,
            selector,
            mode: "auto",
            next: null,
            previous: null,
            shortcuts: []
        }))
        : [];
    registerPageNavigationGraph(root, nodes, wrap);
}

export function unregisterPageNavigation(root) {
    if (!root) return;
    const onKeyDown = pageNavigationBindings.get(root);
    if (!onKeyDown) return;
    root.removeEventListener("keydown", onKeyDown, true);
    pageNavigationBindings.delete(root);
}

// --- Page zoom shortcuts (Ctrl+> / Ctrl+<) -------------------------------
// One document-level listener serves every registered PageControl so a chord
// zooms exactly once per keypress no matter how many page roots are nested.

// Keyed by a stable token from the .NET side, NOT by the root element:
// unregistration must survive the root having already left the DOM (Blazor
// removes the DOM before the dispose-time interop call arrives, so the
// ElementReference revives to null there).
const pageZoomBindings = new Map();
let pageZoomListener = null;

function resolveZoomDirection(event) {
    if (!event.ctrlKey || event.altKey || event.metaKey) return null;
    // Ctrl+> is physically Ctrl+Shift+. on most layouts; browsers report the
    // produced character (">") on some layouts and the base key (".") on others.
    const key = event.key;
    if (key === ">" || (key === "." && event.shiftKey)) return "in";
    if (key === "<" || (key === "," && event.shiftKey)) return "out";
    return null;
}

// Detached roots are also pruned on every touch, as self-healing for circuit
// deaths where the unregister call never arrives at all.
function pruneZoomBindings() {
    for (const [token, binding] of [...pageZoomBindings]) {
        if (!binding.root.isConnected)
            pageZoomBindings.delete(token);
    }
    detachZoomListenerIfIdle();
}

function detachZoomListenerIfIdle() {
    if (pageZoomBindings.size === 0 && pageZoomListener) {
        document.removeEventListener("keydown", pageZoomListener, true);
        pageZoomListener = null;
    }
}

function resolveZoomTarget(event) {
    let match = null;
    let fallback = null;
    for (const binding of pageZoomBindings.values()) {
        if (!binding.root.isConnected) continue;
        fallback = binding.dotNetRef;
        if (event.target instanceof Element && binding.root.contains(event.target))
            match = binding.dotNetRef; // later entries are inner roots — keep the innermost
    }
    return match ?? fallback;
}

export function registerPageZoomShortcuts(root, token, dotNetRef) {
    if (!root || !token || !dotNetRef) return;
    pruneZoomBindings();
    pageZoomBindings.set(token, { root, dotNetRef });

    if (pageZoomListener) return;
    pageZoomListener = event => {
        const direction = resolveZoomDirection(event);
        if (!direction) return;

        pruneZoomBindings();
        const target = resolveZoomTarget(event);
        if (!target) return;

        event.preventDefault();
        event.stopPropagation();
        // Fire-and-forget; the circuit can drop between keydown and dispatch.
        target.invokeMethodAsync("OnZoomShortcutAsync", direction).catch(() => { });
    };
    document.addEventListener("keydown", pageZoomListener, true);
}

export function unregisterPageZoomShortcuts(token) {
    if (token)
        pageZoomBindings.delete(token);
    pruneZoomBindings();
}
