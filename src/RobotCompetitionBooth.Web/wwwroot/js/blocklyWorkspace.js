import { createRobotToolbox, defineRobotBlocks } from "./robotBlockCatalog.js";

const workspaces = new Map();
const collaboratorIdentityStorageKey = "robobooth-collaborator-identity-v1";

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

    const resizeObserver = new ResizeObserver(() => blockly.svgResize(workspace));
    resizeObserver.observe(host);
    workspaces.set(elementId, {
        workspace,
        resizeObserver,
        collaborationReference: null,
        changeListener: null,
        changeTimer: null,
        suppressChanges: false,
        remoteSelectionRoots: new Set()
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
    clearRemoteSelectionStyles(entry);
    entry.resizeObserver.disconnect();
    entry.workspace.dispose();
    workspaces.delete(elementId);
}
