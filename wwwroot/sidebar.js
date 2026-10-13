async function getJSON(url) {
    const resp = await fetch(url);
    if (!resp.ok) {
        alert('Could not load tree data. See console for more details.');
        console.error(await resp.text());
        return [];
    }
    return resp.json();
}

function createTreeNode(id, text, icon, children = false) {
    return { id, text, children, itree: { icon } };
}

function fileIcon(name) {
    const ext = name.split('.').pop().toLowerCase();
    return ['pdf', 'dwg', 'rvt'].includes(ext) ? `icon-item-${ext}` : 'icon-item';
}

// Node IDs are "type|...|region". The hub region is carried down to the version
// so the server can send it to Model Derivative.

async function getHubs() {
    const hubs = await getJSON('/api/hubs');
    return hubs.map(hub => createTreeNode(`hub|${hub.id}|${hub.region}`, hub.name, 'icon-hub', true));
}

async function getProjects(hubId, region) {
    const projects = await getJSON(`/api/hubs/${hubId}/projects`);
    return projects.map(project => createTreeNode(`project|${hubId}|${project.id}|${region}`, project.name, 'icon-project', true));
}

async function getContents(hubId, projectId, region, folderId = null) {
    // The server only returns folders and .dwg, .rvt and .pdf items.
    const contents = await getJSON(`/api/hubs/${hubId}/projects/${projectId}/contents` + (folderId ? `?folder_id=${encodeURIComponent(folderId)}` : ''));
    return contents.map(item => {
        if (item.folder) {
            return createTreeNode(`folder|${hubId}|${projectId}|${region}|${item.id}`, item.name, 'icon-my-folder', true);
        } else {
            return createTreeNode(`item|${hubId}|${projectId}|${region}|${item.id}`, item.name, fileIcon(item.name), true);
        }
    });
}

async function getVersions(hubId, projectId, region, itemId) {
    const versions = await getJSON(`/api/hubs/${hubId}/projects/${projectId}/contents/${encodeURIComponent(itemId)}/versions`);
    return versions.map(version => createTreeNode(`version|${region}|${version.id}`, version.name, 'icon-version'));
}

/**
 * @param {string} selector
 * @param {(selection: { versionId: string, fileName: string, region: string }) => void} onSelectionChanged
 */
export function initTree(selector, onSelectionChanged) {
    // See http://inspire-tree.com
    const tree = new InspireTree({
        data: function (node) {
            if (!node || !node.id) {
                return getHubs();
            }
            const tokens = node.id.split('|');
            switch (tokens[0]) {
                case 'hub': return getProjects(tokens[1], tokens[2]);
                case 'project': return getContents(tokens[1], tokens[2], tokens[3]);
                case 'folder': return getContents(tokens[1], tokens[2], tokens[3], tokens[4]);
                case 'item': return getVersions(tokens[1], tokens[2], tokens[3], tokens[4]);
                default: return [];
            }
        }
    });
    tree.on('node.click', function (event, node) {
        event.preventTreeDefault();
        const tokens = node.id.split('|');
        if (tokens[0] === 'version') {
            const item = node.getParent();
            onSelectionChanged({
                region: tokens[1],
                versionId: tokens.slice(2).join('|'),
                fileName: item ? item.text : ''
            });
        }
    });
    return new InspireTreeDOM(tree, { target: selector });
}
