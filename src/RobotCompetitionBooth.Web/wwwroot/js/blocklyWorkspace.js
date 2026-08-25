import { createRobotToolbox, defineRobotBlocks } from "./robotBlockCatalog.js";

const workspaces = new Map();
const collaboratorIdentityStorageKey = "robobooth-collaborator-identity-v1";
const cursorBroadcastIntervalMilliseconds = 50;

function getBlockly() {
    if (!globalThis.Blockly) {
        throw new Error("Blockly did not load.");
    }

    return globalThis.Blockly;
}

function getEntry(elementId) {
    const entry = workspaces.get(elementId);
    if (!entry) {
        throw new Error("The Blockly workspace is not open.");
    }

    return entry;
}

function addStarterBlock(workspace) {
    const startBlock = workspace.newBlock("prg_on_start");
    startBlock.initSvg();
    startBlock.render();
    startBlock.moveBy(40, 40);
}

function clearRemoteSelectionStyles(entry) {
    for (const root of entry.remoteSelectionRoots) {
        root.style.removeProperty("filter");
        root.style.removeProperty("transition");
    }

    entry.remoteSelectionRoots.clear();
}

function createCursorLayer(host) {
    const layer = document.createElement("div");
    layer.setAttribute("aria-hidden", "true");
    Object.assign(layer.style, {
        position: "absolute",
        zIndex: "60",
        inset: "0",
        overflow: "hidden",
        pointerEvents: "none"
    });
    host.append(layer);
    return layer;
}

function createRemoteCursorElement(entry, cursor) {
    const element = document.createElement("div");
    Object.assign(element.style, {
        position: "absolute",
        top: "0",
        left: "0",
        width: "0",
        height: "0",
        opacity: "0",
        pointerEvents: "none",
        transition: "transform 50ms linear, opacity 100ms ease-out",
        willChange: "transform"
    });

    const marker = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    marker.setAttribute("width", "24");
    marker.setAttribute("height", "28");
    marker.setAttribute("viewBox", "0 0 24 28");
    Object.assign(marker.style, {
        position: "absolute",
        top: "-2px",
        left: "-2px",
        overflow: "visible",
        filter: "drop-shadow(0 1px 1px rgba(15, 23, 42, 0.35))"
    });

    const markerPath = document.createElementNS("http://www.w3.org/2000/svg", "path");
    markerPath.setAttribute("d", "M2 1.5V21L7.8 15.3L12.9 25.8L17.1 23.8L12.1 13.8H20.2Z");
    markerPath.setAttribute("stroke", "white");
    markerPath.setAttribute("stroke-width", "1.5");
    markerPath.setAttribute("stroke-linejoin", "round");
    marker.append(markerPath);

    const label = document.createElement("span");
    Object.assign(label.style, {
        position: "absolute",
        top: "18px",
        left: "17px",
        display: "block",
        maxWidth: "13rem",
        overflow: "hidden",
        padding: "0.22rem 0.45rem",
        border: "1px solid rgba(255, 255, 255, 0.85)",
        borderRadius: "0.35rem",
        color: "white",
        font: "600 0.75rem/1.2 system-ui, sans-serif",
        textOverflow: "ellipsis",
        whiteSpace: "nowrap",
        boxShadow: "0 1px 3px rgba(15, 23, 42, 0.3)"
    });

    element.append(marker, label);
    entry.cursorLayer.append(element);
    const remoteCursor = { element, markerPath, label, cursor };
    updateRemoteCursorAppearance(remoteCursor, cursor);
    return remoteCursor;
}

function updateRemoteCursorAppearance(remoteCursor, cursor) {
    remoteCursor.cursor = cursor;
    remoteCursor.markerPath.setAttribute("fill", cursor.color);
    remoteCursor.label.style.background = cursor.color;
    remoteCursor.label.textContent = cursor.name;
}

function positionRemoteCursor(entry, remoteCursor) {
    const blockly = getBlockly();
    const hostBounds = entry.host.getBoundingClientRect();
    const screenPosition = blockly.utils.svgMath.wsToScreenCoordinates(
        entry.workspace,
        new blockly.utils.Coordinate(
            remoteCursor.cursor.workspaceX,
            remoteCursor.cursor.workspaceY));
    const left = screenPosition.x - hostBounds.left;
    const top = screenPosition.y - hostBounds.top;
    const isVisible =
        left >= 0 && left <= hostBounds.width &&
        top >= 0 && top <= hostBounds.height;

    remoteCursor.element.style.opacity = isVisible ? "1" : "0";
    remoteCursor.element.style.transform = `translate3d(${left}px, ${top}px, 0)`;
    remoteCursor.label.style.left = left > hostBounds.width - 180 ? "auto" : "17px";
    remoteCursor.label.style.right = left > hostBounds.width - 180 ? "7px" : "auto";
    remoteCursor.label.style.top = top > hostBounds.height - 55 ? "auto" : "18px";
    remoteCursor.label.style.bottom = top > hostBounds.height - 55 ? "5px" : "auto";
}

function positionRemoteCursors(entry) {
    for (const remoteCursor of entry.remoteCursors.values()) {
        positionRemoteCursor(entry, remoteCursor);
    }
}

function clearRemoteCursors(entry) {
    for (const remoteCursor of entry.remoteCursors.values()) {
        remoteCursor.element.remove();
    }

    entry.remoteCursors.clear();
}

function invokeCursorChanged(entry, workspaceX, workspaceY) {
    const reference = entry.collaborationReference;
    if (!reference) {
        return;
    }

    reference
        .invokeMethodAsync("OnLocalCursorChanged", workspaceX, workspaceY)
        .catch(() => {});
}

function broadcastPendingCursor(entry) {
    entry.cursorBroadcastTimer = null;
    const cursor = entry.pendingLocalCursor;
    entry.pendingLocalCursor = null;
    if (!cursor) {
        return;
    }

    entry.lastCursorBroadcastAt = performance.now();
    entry.localCursorVisible = true;
    invokeCursorChanged(entry, cursor.x, cursor.y);
}

function scheduleCursorBroadcast(entry, workspaceX, workspaceY) {
    entry.pendingLocalCursor = { x: workspaceX, y: workspaceY };
    if (entry.cursorBroadcastTimer) {
        return;
    }

    const elapsed = performance.now() - entry.lastCursorBroadcastAt;
    const delay = Math.max(0, cursorBroadcastIntervalMilliseconds - elapsed);
    entry.cursorBroadcastTimer = setTimeout(
        () => broadcastPendingCursor(entry),
        delay);
}

function hideLocalCursor(entry) {
    if (entry.cursorBroadcastTimer) {
        clearTimeout(entry.cursorBroadcastTimer);
        entry.cursorBroadcastTimer = null;
    }
    entry.pendingLocalCursor = null;

    if (!entry.localCursorVisible) {
        return;
    }

    entry.localCursorVisible = false;
    invokeCursorChanged(entry, null, null);
}

function stopCursorTracking(entry) {
    hideLocalCursor(entry);
    if (entry.cursorMoveListener) {
        entry.host.removeEventListener("pointermove", entry.cursorMoveListener);
        entry.host.removeEventListener("pointerleave", entry.cursorLeaveListener);
        entry.host.removeEventListener("pointercancel", entry.cursorLeaveListener);
        window.removeEventListener("blur", entry.cursorLeaveListener);
        document.removeEventListener("visibilitychange", entry.visibilityListener);
    }

    entry.cursorMoveListener = null;
    entry.cursorLeaveListener = null;
    entry.visibilityListener = null;
}

function startCursorTracking(entry) {
    stopCursorTracking(entry);
    const blockly = getBlockly();
    entry.cursorMoveListener = event => {
        if (event.pointerType === "touch") {
            return;
        }

        const target = event.target;
        if (target instanceof Element &&
            target.closest(".blocklyToolbox, .blocklyFlyout")) {
            hideLocalCursor(entry);
            return;
        }

        const workspacePosition = blockly.utils.svgMath.screenToWsCoordinates(
            entry.workspace,
            new blockly.utils.Coordinate(event.clientX, event.clientY));
        scheduleCursorBroadcast(entry, workspacePosition.x, workspacePosition.y);
    };
    entry.cursorLeaveListener = () => hideLocalCursor(entry);
    entry.visibilityListener = () => {
        if (document.hidden) {
            hideLocalCursor(entry);
        }
    };
    entry.host.addEventListener("pointermove", entry.cursorMoveListener);
    entry.host.addEventListener("pointerleave", entry.cursorLeaveListener);
    entry.host.addEventListener("pointercancel", entry.cursorLeaveListener);
    window.addEventListener("blur", entry.cursorLeaveListener);
    document.addEventListener("visibilitychange", entry.visibilityListener);
}

function runWithoutBroadcast(entry, action) {
    const blockly = getBlockly();
    entry.suppressChanges = true;
    blockly.Events.disable();
    try {
        clearRemoteSelectionStyles(entry);
        action();
    } finally {
        blockly.Events.enable();
        entry.suppressChanges = false;
        positionRemoteCursors(entry);
    }
}

export function create(elementId, initialStateJson) {
    dispose(elementId);

    const blockly = getBlockly();
    defineRobotBlocks(blockly);

    const host = document.getElementById(elementId);
    if (!host) {
        throw new Error("The Blockly workspace element could not be found.");
    }

    const workspace = blockly.inject(host, {
        toolbox: createRobotToolbox(),
        media: "/lib/blockly/media/",
        renderer: "zelos",
        grid: {
            spacing: 20,
            length: 3,
            colour: "#d5d9dd",
            snap: true
        },
        zoom: {
            controls: true,
            wheel: true,
            startScale: 0.9,
            maxScale: 1.8,
            minScale: 0.45,
            scaleSpeed: 1.1
        },
        move: {
            scrollbars: true,
            drag: true,
            wheel: true
        },
        trashcan: true
    });

    if (initialStateJson) {
        blockly.serialization.workspaces.load(JSON.parse(initialStateJson), workspace);
    } else {
        addStarterBlock(workspace);
    }

    const cursorLayer = createCursorLayer(host);
    const resizeObserver = new ResizeObserver(() => {
        blockly.svgResize(workspace);
        const entry = workspaces.get(elementId);
        if (entry) {
            positionRemoteCursors(entry);
        }
    });
    resizeObserver.observe(host);
    workspaces.set(elementId, {
        host,
        workspace,
        resizeObserver,
        cursorLayer,
        collaborationReference: null,
        changeListener: null,
        changeTimer: null,
        cursorMoveListener: null,
        cursorLeaveListener: null,
        visibilityListener: null,
        cursorBroadcastTimer: null,
        pendingLocalCursor: null,
        lastCursorBroadcastAt: 0,
        localCursorVisible: false,
        suppressChanges: false,
        remoteSelectionRoots: new Set(),
        remoteCursors: new Map()
    });
    blockly.svgResize(workspace);
}

export function save(elementId) {
    const blockly = getBlockly();
    const { workspace } = getEntry(elementId);
    return JSON.stringify(blockly.serialization.workspaces.save(workspace));
}

export function load(elementId, stateJson) {
    const blockly = getBlockly();
    const entry = getEntry(elementId);
    runWithoutBroadcast(entry, () => {
        entry.workspace.clear();
        blockly.serialization.workspaces.load(JSON.parse(stateJson), entry.workspace);
    });
}

export function reset(elementId) {
    const entry = getEntry(elementId);
    runWithoutBroadcast(entry, () => {
        entry.workspace.clear();
        addStarterBlock(entry.workspace);
    });
}

export function getStoredCollaboratorIdentity() {
    try {
        const storedValue = localStorage.getItem(collaboratorIdentityStorageKey);
        if (!storedValue) {
            return null;
        }

        const identity = JSON.parse(storedValue);
        return identity && typeof identity === "object" ? identity : null;
    } catch {
        return null;
    }
}

export function storeCollaboratorIdentity(identity) {
    try {
        localStorage.setItem(collaboratorIdentityStorageKey, JSON.stringify({
            id: identity.id,
            name: identity.name,
            color: identity.color
        }));
    } catch {
        // Collaboration still works when browser storage is unavailable.
    }
}

export function startCollaboration(elementId, dotNetReference) {
    const blockly = getBlockly();
    const entry = getEntry(elementId);
    if (entry.changeListener) {
        entry.workspace.removeChangeListener(entry.changeListener);
    }

    entry.collaborationReference = dotNetReference;
    startCursorTracking(entry);
    entry.changeListener = event => {
        if (entry.suppressChanges) {
            return;
        }

        if (event.type === (blockly.Events.SELECTED ?? "selected")) {
            const blockId = event.newElementId ?? null;
            const block = blockId ? entry.workspace.getBlockById(blockId) : null;
            const description = block ? block.toString(100) : null;
            dotNetReference
                .invokeMethodAsync("OnLocalSelectionChanged", blockId, description)
                .catch(() => {});
            return;
        }

        if (event.isUiEvent) {
            positionRemoteCursors(entry);
            return;
        }

        if (entry.changeTimer) {
            clearTimeout(entry.changeTimer);
        }

        entry.changeTimer = setTimeout(() => {
            entry.changeTimer = null;
            if (entry.suppressChanges || entry.collaborationReference !== dotNetReference) {
                return;
            }

            const stateJson = JSON.stringify(
                blockly.serialization.workspaces.save(entry.workspace));
            dotNetReference
                .invokeMethodAsync("OnLocalWorkspaceChanged", stateJson)
                .catch(() => {});
        }, 100);
    };
    entry.workspace.addChangeListener(entry.changeListener);
}

export function applyRemoteState(elementId, stateJson) {
    load(elementId, stateJson);
}

export function setRemoteSelections(elementId, selections) {
    const entry = getEntry(elementId);
    clearRemoteSelectionStyles(entry);

    for (const selection of selections) {
        if (!selection.blockId) {
            continue;
        }

        const block = entry.workspace.getBlockById(selection.blockId);
        const root = block?.getSvgRoot();
        if (!root) {
            continue;
        }

        root.style.setProperty(
            "filter",
            `drop-shadow(0 0 2px ${selection.color}) drop-shadow(0 0 2px ${selection.color})`);
        root.style.setProperty("transition", "filter 120ms ease-out");
        entry.remoteSelectionRoots.add(root);
    }
}

export function setRemoteCursors(elementId, cursors) {
    const entry = getEntry(elementId);
    clearRemoteCursors(entry);
    for (const cursor of cursors) {
        const remoteCursor = createRemoteCursorElement(entry, cursor);
        entry.remoteCursors.set(cursor.collaboratorId, remoteCursor);
        positionRemoteCursor(entry, remoteCursor);
    }
}

function removeRemoteCursor(entry, collaboratorId, animateRemoval) {
    const remoteCursor = entry.remoteCursors.get(collaboratorId);
    if (!remoteCursor) {
        return;
    }

    entry.remoteCursors.delete(collaboratorId);
    const shouldAnimate =
        animateRemoval &&
        remoteCursor.element.style.opacity === "1" &&
        typeof remoteCursor.element.animate === "function";
    if (!shouldAnimate) {
        remoteCursor.element.remove();
        return;
    }

    const restingTransform = remoteCursor.element.style.transform;
    const animation = remoteCursor.element.animate([
        { transform: `${restingTransform} scale(1)`, opacity: 1 },
        { transform: `${restingTransform} scale(1.06)`, opacity: 0.9, offset: 0.45 },
        { transform: `${restingTransform} scale(0.86)`, opacity: 0 }
    ], {
        duration: 180,
        easing: "cubic-bezier(0.2, 0.75, 0.35, 1)",
        fill: "forwards"
    });
    animation.finished
        .catch(() => {})
        .finally(() => remoteCursor.element.remove());
}

export function setRemoteCursor(
    elementId,
    collaboratorId,
    cursor,
    collaboratorLeft = false) {
    const entry = getEntry(elementId);
    const existingCursor = entry.remoteCursors.get(collaboratorId);
    if (!cursor) {
        removeRemoteCursor(entry, collaboratorId, collaboratorLeft);
        return;
    }

    const remoteCursor = existingCursor ?? createRemoteCursorElement(entry, cursor);
    if (!existingCursor) {
        entry.remoteCursors.set(collaboratorId, remoteCursor);
    }
    updateRemoteCursorAppearance(remoteCursor, cursor);
    positionRemoteCursor(entry, remoteCursor);
}

export function dispose(elementId) {
    const entry = workspaces.get(elementId);
    if (!entry) {
        return;
    }

    if (entry.changeTimer) {
        clearTimeout(entry.changeTimer);
    }
    if (entry.changeListener) {
        entry.workspace.removeChangeListener(entry.changeListener);
    }
    stopCursorTracking(entry);
    clearRemoteSelectionStyles(entry);
    clearRemoteCursors(entry);
    entry.resizeObserver.disconnect();
    entry.cursorLayer.remove();
    entry.workspace.dispose();
    workspaces.delete(elementId);
}
