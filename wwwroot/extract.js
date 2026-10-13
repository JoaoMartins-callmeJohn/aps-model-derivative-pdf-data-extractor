import { extractViewerStrings } from './viewer.js';

const button = document.getElementById('extract');
const counter = document.getElementById('counter');
const counterValue = document.getElementById('counter-value');
const historyRows = document.getElementById('history-rows');
const breakdown = document.getElementById('breakdown');

function decodeHeader(resp, name, fallback) {
    const value = resp.headers.get(name);
    if (!value) return fallback;
    try {
        const bytes = Uint8Array.from(atob(value), c => c.charCodeAt(0));
        return JSON.parse(new TextDecoder().decode(bytes));
    } catch {
        return fallback;
    }
}

function fileNameFromResponse(resp, fallback) {
    const header = resp.headers.get('Content-Disposition') || '';
    const utf8 = /filename\*=UTF-8''([^;]+)/i.exec(header);
    if (utf8) return decodeURIComponent(utf8[1]);
    const plain = /filename="?([^";]+)"?/i.exec(header);
    return plain ? plain[1] : fallback;
}

function download(blob, name) {
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

function cell(row, text, className) {
    const td = document.createElement('td');
    td.textContent = text;
    if (className) td.className = className;
    row.appendChild(td);
}

function showCounter(count, failed) {
    counterValue.textContent = count;
    counter.classList.toggle('has-value', !failed);
    counter.classList.toggle('error', !!failed);
}

function showBreakdown(title, rows, total, notes) {
    document.getElementById('breakdown-title').textContent = title;
    const body = document.getElementById('breakdown-rows');
    body.innerHTML = '';
    for (const r of rows) {
        const tr = document.createElement('tr');
        cell(tr, r.endpoint);
        cell(tr, r.count, 'num');
        cell(tr, r.statuses);
        cell(tr, r.totalMs, 'num');
        body.appendChild(tr);
    }
    document.getElementById('breakdown-total').textContent = total;
    const notesDiv = document.getElementById('breakdown-notes');
    notesDiv.innerHTML = '';
    for (const note of notes) {
        const div = document.createElement('div');
        div.textContent = note.text;
        if (note.warning) div.className = 'warning';
        notesDiv.appendChild(div);
    }
    breakdown.hidden = false;
}

function addHistory(entry) {
    const empty = historyRows.querySelector('tr.empty');
    if (empty) empty.remove();
    const tr = document.createElement('tr');
    if (entry.failed) tr.className = 'failed';
    cell(tr, entry.time.toLocaleTimeString());
    cell(tr, `${entry.fileName} / ${entry.viewName}`);
    cell(tr, entry.count, 'num');
    cell(tr, entry.textSource);
    tr.title = entry.title;
    tr.style.cursor = 'pointer';
    tr.onclick = () => showBreakdown(entry.title, entry.rows, entry.count, entry.notes);
    historyRows.prepend(tr);
}

/**
 * Wires the "Extract" button.
 * The browser only sends the URN and view GUID (plus the Viewer strings as a fallback).
 * The server makes every Model Derivative call and reports the count in response headers.
 */
export function initExtract(viewer, state, setStatus) {
    const refresh = () => { button.disabled = !(state.urn && state.current); };
    document.addEventListener('view-changed', refresh);
    counter.onclick = () => { if (counterValue.textContent !== '–') breakdown.hidden = !breakdown.hidden; };
    document.getElementById('breakdown-close').onclick = () => { breakdown.hidden = true; };

    button.onclick = async () => {
        const { urn, region, fileName } = state;
        const view = state.current;
        if (!urn || !view) return;
        button.disabled = true;
        const title = `${fileName} / ${view.name}`;
        try {
            setStatus('Reading Viewer strings (fallback text source)...', true);
            const clientStrings = await extractViewerStrings(viewer);

            setStatus(`Extracting ${title} on the server...`, true);
            const resp = await fetch('/api/extract', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ urn, viewGuid: view.guid, viewName: view.name, fileName, region, clientStrings })
            });

            if (!resp.ok) {
                const err = await resp.json().catch(() => ({ error: resp.statusText }));
                const count = err.mdCallCount ?? 0;
                const rows = err.mdCallBreakdown ?? [];
                const notes = [{ text: `Failed: ${err.error}`, warning: true }];
                showCounter(count, true);
                showBreakdown(title, rows, count, notes);
                addHistory({ time: new Date(), fileName, viewName: view.name, count, textSource: 'failed', title, rows, notes, failed: true });
                setStatus(`Extraction failed: ${err.error}`);
                return;
            }

            const blob = await resp.blob();
            const name = fileNameFromResponse(resp, `${fileName}_${view.name}.sdb`);
            download(blob, name);

            const count = parseInt(resp.headers.get('X-MD-Call-Count') || '0', 10);
            const rows = decodeHeader(resp, 'X-MD-Call-Breakdown', []);
            const warnings = decodeHeader(resp, 'X-Warnings', []);
            const textSource = resp.headers.get('X-Text-Source') || '?';
            const notes = [
                { text: `File: ${name} (${(blob.size / 1024).toFixed(1)} KB)` },
                { text: `Objects: ${resp.headers.get('X-Object-Count')}, text runs: ${resp.headers.get('X-Text-Count')}, text source: ${textSource}` },
                { text: 'The PDF download from the CDN is not a Model Derivative call and is not counted.' },
                ...warnings.map(w => ({ text: w, warning: true }))
            ];
            showCounter(count, false);
            showBreakdown(title, rows, count, notes);
            addHistory({ time: new Date(), fileName, viewName: view.name, count, textSource, title, rows, notes });
            setStatus(`${name} generated with ${count} Model Derivative calls.`);
        } catch (err) {
            console.error(err);
            setStatus(`Extraction failed: ${err.message}`);
        } finally {
            refresh();
        }
    };
}
