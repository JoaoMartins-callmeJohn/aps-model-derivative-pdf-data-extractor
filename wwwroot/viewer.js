async function getAccessToken(callback) {
    try {
        const resp = await fetch('/api/auth/token');
        if (!resp.ok)
            throw new Error(await resp.text());
        const { access_token, expires_in } = await resp.json();
        callback(access_token, expires_in);
    } catch (err) {
        alert('Could not obtain access token. See the console for more details.');
        console.error(err);
    }
}

export function initViewer(container) {
    return new Promise(function (resolve) {
        Autodesk.Viewing.Initializer({ env: 'AutodeskProduction2', api: 'streamingV2', getAccessToken }, function () {
            // No DocumentBrowser: the toolbar dropdown lists 2D views only.
            const viewer = new Autodesk.Viewing.GuiViewer3D(container, { extensions: [] });
            viewer.start();
            viewer.setTheme('light-theme');
            resolve(viewer);
        });
    });
}

/** Converts a Data Management version ID to a URL-safe base64 URN. */
export function toUrn(versionId) {
    return window.btoa(versionId).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function loadDocument(urn) {
    return new Promise((resolve, reject) => {
        Autodesk.Viewing.Document.load('urn:' + urn, resolve, (code, message) => reject(new Error(`${message} (code ${code})`)));
    });
}

/**
 * Loads a document and returns its 2D views only.
 * For RVT and DWG, 3D views are filtered out. For PDF, every page is a 2D view.
 * @returns {Promise<{ doc: Autodesk.Viewing.Document, views: Autodesk.Viewing.BubbleNode[] }>}
 */
export async function loadDocument2D(urn, region) {
    // Files in EMEA hubs are served from the EU endpoint.
    try {
        const ep = Autodesk.Viewing.endpoint;
        ep.setEndpointAndApi(ep.getApiEndpoint(), region === 'EMEA' ? 'streamingV2_EU' : 'streamingV2');
    } catch (err) {
        console.warn('Could not switch the Viewer endpoint for region', region, err);
    }
    const doc = await loadDocument(urn);
    const views = doc.getRoot().search({ type: 'geometry', role: '2d' });
    return { doc, views };
}

/** Loads one 2D view. String fetching is enabled so the StringExtractor fallback can read text. */
export async function loadView(viewer, doc, node) {
    if (viewer.model) {
        viewer.unloadModel(viewer.model);
    }
    const model = await viewer.loadDocumentNode(doc, node, { extendStringsFetching: true });
    await viewer.waitForLoadDone();
    return model;
}

/**
 * Fallback text source, as in https://aps.autodesk.com/blog/retrieving-text-2d-views.
 * The server only uses these strings when the view has no PDF derivative.
 */
export async function extractViewerStrings(viewer) {
    if (!viewer.model) {
        return [];
    }
    try {
        const ext = viewer.getExtension('Autodesk.StringExtractor') || await viewer.loadExtension('Autodesk.StringExtractor');
        await ext.extractStringsFromModels();
        const all = Object.values(ext.documentStrings || {}).flatMap(entry => {
            if (Array.isArray(entry)) return entry;
            if (entry && Array.isArray(entry.strings)) return entry.strings;
            return [];
        });
        return all
            .filter(s => s && s.string && s.boundingBox)
            .map(s => ({
                string: s.string,
                minX: s.boundingBox.min.x,
                minY: s.boundingBox.min.y,
                maxX: s.boundingBox.max.x,
                maxY: s.boundingBox.max.y,
                angle: s.angle || 0,
                stringHeight: s.stringHeight || 0
            }));
    } catch (err) {
        console.warn('Autodesk.StringExtractor could not read strings for this view.', err);
        return [];
    }
}
