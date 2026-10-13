import { initViewer, loadDocument2D, loadView, toUrn } from './viewer.js';
import { initTree } from './sidebar.js';
import { initExtract } from './extract.js';

const login = document.getElementById('login');
const viewsSelect = document.getElementById('views');
const status = document.getElementById('status');

// What is currently shown in the viewer. Read by extract.js.
const state = { urn: null, region: null, fileName: null, doc: null, views: [], current: null };

function setStatus(text, busy = false) {
    status.textContent = text;
    status.classList.toggle('busy', busy);
}

function fillViews(views) {
    viewsSelect.innerHTML = '';
    for (const [index, node] of views.entries()) {
        const option = document.createElement('option');
        option.value = index;
        option.textContent = node.name() || `View ${index + 1}`;
        viewsSelect.appendChild(option);
    }
    viewsSelect.disabled = views.length === 0;
}

async function showView(viewer, index) {
    const node = state.views[index];
    state.current = null;
    setStatus(`Loading ${node.name()}...`, true);
    await loadView(viewer, state.doc, node);
    state.current = { guid: node.guid(), name: node.name() };
    setStatus('');
    document.dispatchEvent(new CustomEvent('view-changed'));
}

async function openFile(viewer, { versionId, fileName, region }) {
    state.urn = toUrn(versionId);
    state.region = region;
    state.fileName = fileName;
    state.current = null;
    state.views = [];
    fillViews([]);
    document.dispatchEvent(new CustomEvent('view-changed'));
    setStatus(`Opening ${fileName}...`, true);
    try {
        const { doc, views } = await loadDocument2D(state.urn, region);
        state.doc = doc;
        // RVT and DWG: only 2D views (sheets, layouts) are listed. PDF: one view per page.
        state.views = views;
        fillViews(views);
        if (views.length === 0) {
            if (viewer.model) viewer.unloadModel(viewer.model);
            setStatus(`${fileName} has no 2D views.`);
            return;
        }
        await showView(viewer, 0);
    } catch (err) {
        console.error(err);
        setStatus(`Could not open ${fileName}: ${err.message}. The file may not be translated yet.`);
    }
}

try {
    const resp = await fetch('/api/auth/profile');
    if (resp.ok) {
        const user = await resp.json();
        login.innerText = `Logout (${user.name})`;
        login.onclick = () => {
            const iframe = document.createElement('iframe');
            iframe.style.visibility = 'hidden';
            iframe.src = 'https://accounts.autodesk.com/Authentication/LogOut';
            document.body.appendChild(iframe);
            iframe.onload = () => {
                window.location.replace('/api/auth/logout');
                document.body.removeChild(iframe);
            };
        };
        const viewer = await initViewer(document.getElementById('preview'));
        viewsSelect.onchange = () => showView(viewer, parseInt(viewsSelect.value, 10)).catch(err => {
            console.error(err);
            setStatus(`Could not load the view: ${err.message}`);
        });
        initTree('#tree', selection => openFile(viewer, selection));
        initExtract(viewer, state, setStatus);
    } else {
        login.innerText = 'Login';
        login.onclick = () => window.location.replace('/api/auth/login');
    }
    login.style.visibility = 'visible';
} catch (err) {
    alert('Could not initialize the application. See console for more details.');
    console.error(err);
}
