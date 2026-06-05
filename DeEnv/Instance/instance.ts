// Client-side instance driver.
// Connects over WebSocket, handles checkbox save-on-change and client-side navigation.

type NodeData =
  | { kind: 'bool';       value: boolean }
  | { kind: 'int';        value: number }
  | { kind: 'decimal';    value: number }
  | { kind: 'text';       value: string }
  | { kind: 'date';       value: string }
  | { kind: 'datetime';   value: string }
  | { kind: 'object';     fields: Record<string, NodeData> }
  | { kind: 'dictionary'; entries: Array<{ key: NodeData; value: NodeData }> };

type ReadResponse  = { op: 'read';  path: string; data?: NodeData; notFound?: boolean };
type WriteResponse = { op: 'write'; path: string; ok?: boolean; error?: string };
type ServerMessage = ReadResponse | WriteResponse | { error: string };

const ws = new WebSocket(`ws://${window.location.host}/ws`);

ws.addEventListener('open', () => {
    attachHandlers();
});

ws.addEventListener('message', (event: MessageEvent<string>) => {
    const msg = JSON.parse(event.data) as ServerMessage;
    if ('error' in msg && !('op' in msg)) {
        console.error('Server error:', msg.error);
        return;
    }
    if (msg.op === 'read') {
        if (msg.notFound) {
            window.location.href = msg.path; // fall back to SSR
        } else if (msg.data) {
            applyNavigation(msg.path, msg.data);
        }
    }
});

// ── handler attachment ────────────────────────────────────────────────────────

function attachHandlers(): void {
    // Bool checkboxes: save immediately on change.
    document.querySelectorAll<HTMLInputElement>('input[type="checkbox"][data-path]').forEach(cb => {
        cb.addEventListener('change', () => {
            const path = cb.dataset['path'] ?? '/';
            ws.send(JSON.stringify({ op: 'write', path, value: cb.checked }));
        });
    });

    // Dictionary table rows: navigate on click (client-side).
    document.querySelectorAll<HTMLTableRowElement>('tr[data-nav]').forEach(row => {
        row.addEventListener('click', (e: MouseEvent) => {
            if ((e.target as HTMLElement).tagName === 'A') return;
            const path = row.dataset['nav']!;
            ws.send(JSON.stringify({ op: 'read', path }));
        });
    });

    // Object forms: save on submit.
    document.querySelectorAll<HTMLFormElement>('form[id="node-form"]').forEach(form => {
        form.addEventListener('submit', (e: SubmitEvent) => {
            e.preventDefault();
            saveForm(form);
        });
    });
}

// ── form save ─────────────────────────────────────────────────────────────────

function saveForm(form: HTMLFormElement): void {
    form.querySelectorAll<HTMLInputElement | HTMLSelectElement>('[data-path]').forEach(el => {
        if (!(el instanceof HTMLInputElement)) return;
        const path = el.dataset['path']!;
        let value: unknown;
        if (el.type === 'checkbox') {
            value = el.checked;
        } else if (el.type === 'number') {
            value = el.valueAsNumber;
        } else {
            value = el.value;
        }
        ws.send(JSON.stringify({ op: 'write', path, value }));
    });
}

// ── client-side navigation ────────────────────────────────────────────────────

function applyNavigation(path: string, data: NodeData): void {
    history.pushState(null, '', path);
    const main = document.querySelector('main');
    if (!main) return;
    renderInto(main, path, data);
    attachHandlers();
}

window.addEventListener('popstate', () => {
    ws.send(JSON.stringify({ op: 'read', path: window.location.pathname }));
});

// ── client-side rendering ─────────────────────────────────────────────────────

function renderInto(container: Element, path: string, data: NodeData): void {
    switch (data.kind) {
        case 'bool':
            container.innerHTML = boolHtml(path, data.value);
            break;
        case 'object':
            container.innerHTML = objectHtml(path, data.fields);
            break;
        case 'dictionary':
            container.innerHTML = dictionaryHtml(path, data.entries);
            break;
        default:
            container.innerHTML = `<p>${escapeHtml(String(data.value))}</p>`;
    }
}

function boolHtml(path: string, value: boolean): string {
    const checked = value ? ' checked' : '';
    const p = escapeHtml(path);
    return `<form id="node-form" data-path="${p}">
  <label>
    <input type="checkbox" id="node-bool"${checked} data-path="${p}">
    Db
  </label>
</form>`;
}

function objectHtml(path: string, fields: Record<string, NodeData>): string {
    const p = escapeHtml(path);
    const rows = Object.entries(fields).map(([name, val]) => {
        const fieldPath = `${path}/${name}`.replace('//', '/');
        return `<div class="field"><label>${escapeHtml(name)}</label>${fieldHtml(fieldPath, val)}</div>`;
    }).join('\n');
    return `<form id="node-form" data-path="${p}">${rows}<div class="actions"><button type="submit">Save</button></div></form>`;
}

function fieldHtml(path: string, data: NodeData): string {
    const p = escapeHtml(path);
    if (data.kind === 'bool') {
        const checked = data.value ? ' checked' : '';
        return `<input type="checkbox" data-path="${p}"${checked}>`;
    }
    if (data.kind === 'dictionary') {
        return dictionaryHtml(path, data.entries);
    }
    const v = escapeHtml(String('value' in data ? data.value : ''));
    return `<input type="text" data-path="${p}" value="${v}">`;
}

function dictionaryHtml(path: string, entries: Array<{ key: NodeData; value: NodeData }>): string {
    const rows = entries.map(({ key, value }) => {
        const keyStr = escapeHtml(String('value' in key ? key.value : ''));
        const entryPath = escapeHtml(`${path}/${keyStr}`.replace('//', '/'));
        const cellContent = value.kind === 'object'
            ? Object.values(value.fields).map(f => `<td>${escapeHtml(String('value' in f ? f.value : ''))}</td>`).join('')
            : `<td>${escapeHtml(String('value' in value ? value.value : ''))}</td>`;
        return `<tr data-nav="${entryPath}"><td><a href="${entryPath}">${keyStr}</a></td>${cellContent}</tr>`;
    }).join('\n');
    return `<table><tbody>${rows}</tbody></table>`;
}

function escapeHtml(s: string): string {
    return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}
