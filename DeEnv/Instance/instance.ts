// Client-side instance driver.
// Navigation is plain SSR (links). The WebSocket carries all mutations
// (write / writeObject / addEntry / removeEntry) as request/response pairs,
// and fetches a blank entry template for the transient "create" form.

type NodeData =
  | { type: 'bool';     value: boolean }
  | { type: 'int';      value: number }
  | { type: 'decimal';  value: number }
  | { type: 'text';     value: string }
  | { type: 'date';     value: string }
  | { type: 'datetime'; value: string }
  | { type: 'object';   typeName?: string; id?: number; fields?: Record<string, NodeData> }
  | { type: 'set';      members: Record<string, NodeData> }
  | { type: 'dictionary'; entries: Array<{ key: NodeData; value: NodeData }> };

type Reply = Record<string, unknown> & { id?: number; error?: string };

// ── WebSocket request/response layer ───────────────────────────────────────────

const ws = new WebSocket(
    `${window.location.protocol === 'https:' ? 'wss:' : 'ws:'}//${window.location.host}/ws`);
let nextId = 1;
const pending = new Map<number, (reply: Reply) => void>();

const ready = new Promise<void>(resolve => {
    ws.addEventListener('open', () => resolve());
});

ws.addEventListener('message', (event: MessageEvent<string>) => {
    const reply = JSON.parse(event.data) as Reply;
    if (typeof reply.id === 'number' && pending.has(reply.id)) {
        pending.get(reply.id)!(reply);
        pending.delete(reply.id);
    }
});

async function call(message: Record<string, unknown>): Promise<Reply> {
    await ready;
    const id = nextId++;
    return new Promise<Reply>(resolve => {
        pending.set(id, resolve);
        ws.send(JSON.stringify({ ...message, id }));
    });
}

// ── handler attachment (runs on first paint and after client renders) ──────────

function attachHandlers(root: ParentNode = document): void {
    root.querySelectorAll<HTMLFormElement>('form#node-form').forEach(form => {
        if (form.dataset['wired']) return;
        form.dataset['wired'] = '1';
        form.addEventListener('submit', e => { e.preventDefault(); void saveForm(form); });
    });

    root.querySelectorAll<HTMLButtonElement>('button[data-newentry]').forEach(btn => {
        if (btn.dataset['wired']) return;
        btn.dataset['wired'] = '1';
        btn.addEventListener('click', () => void openCreateForm(btn));
    });

    root.querySelectorAll<HTMLButtonElement>('button[data-delentry]').forEach(btn => {
        if (btn.dataset['wired']) return;
        btn.dataset['wired'] = '1';
        btn.addEventListener('click', () => void deleteEntry(btn));
    });

    // Reference editor: the mode buttons switch between pick-existing and create-new.
    root.querySelectorAll<HTMLButtonElement>('[data-ref] button[data-mode]').forEach(btn => {
        if (btn.dataset['wired']) return;
        btn.dataset['wired'] = '1';
        btn.addEventListener('click', e => {
            e.preventDefault();
            const editor = btn.closest<HTMLElement>('[data-ref]');
            if (editor) { editor.dataset['current'] = btn.dataset['mode']!; updateRefMode(editor); }
        });
    });
    root.querySelectorAll<HTMLElement>('[data-ref]').forEach(updateRefMode);
}

// Show only the section for the active mode (existing vs new).
function updateRefMode(editor: HTMLElement): void {
    const mode = editor.dataset['current'] ?? 'existing';
    editor.querySelector<HTMLElement>('.ref-existing')?.style.setProperty('display', mode === 'existing' ? '' : 'none');
    editor.querySelector<HTMLElement>('.ref-new')?.style.setProperty('display', mode === 'new' ? '' : 'none');
}

ready.then(() => attachHandlers());

// ── reading inputs ─────────────────────────────────────────────────────────────

function readInput(el: HTMLInputElement): unknown {
    if (el.type === 'checkbox') return el.checked;
    if (el.type === 'number')   return el.valueAsNumber;
    return el.value; // text, date (yyyy-mm-dd string)
}

// Collect a writeObject `fields` map from inputs carrying data-field.
function collectFields(scope: ParentNode): Record<string, unknown> {
    const fields: Record<string, unknown> = {};
    scope.querySelectorAll<HTMLInputElement>('input[data-field]').forEach(el => {
        fields[el.dataset['field']!] = readInput(el);
    });
    return fields;
}

// ── save (existing node) ───────────────────────────────────────────────────────

async function saveForm(form: HTMLFormElement): Promise<void> {
    const path = form.dataset['path'] ?? '/';
    if (form.dataset['kind'] === 'leaf') {
        const input = form.querySelector<HTMLInputElement>('input[data-path]');
        if (!input) return;
        await call({ op: 'write', path, value: readInput(input) });
    } else if (form.dataset['kind'] === 'reference') {
        await saveReference(form, path);
    } else {
        await call({ op: 'writeObject', path, fields: collectFields(form) });
    }
}

// Save a reference field: point at the picked existing object, or mint a new one.
async function saveReference(form: HTMLFormElement, path: string): Promise<void> {
    const editor = form.querySelector<HTMLElement>('[data-ref]');
    const mode = editor?.dataset['current'] ?? 'existing';
    let msg: Record<string, unknown>;
    if (mode === 'new') {
        const fields: Record<string, unknown> = {};
        editor!.querySelectorAll<HTMLInputElement>('.ref-new input[name]').forEach(el => {
            fields[el.getAttribute('name')!] = readInput(el);
        });
        msg = { op: 'setReference', path, value: fields };
    } else {
        const sel = editor!.querySelector<HTMLSelectElement>('select[data-pick]');
        msg = { op: 'setReference', path, refId: Number(sel?.value) };
    }
    const reply = await call(msg);
    if (reply.error) { alert(reply.error); return; }
    location.reload();
}

// ── delete entry ─────────────────────────────────────────────────────────────

async function deleteEntry(btn: HTMLButtonElement): Promise<void> {
    const path = btn.dataset['delentry']!;
    const key = btn.dataset['key']!;
    const reply = await call({ op: 'removeEntry', path, key });
    if (reply.error) { alert(reply.error); return; }
    location.reload();
}

// ── create entry (transient client form) ───────────────────────────────────────

async function openCreateForm(btn: HTMLButtonElement): Promise<void> {
    const dictPath = btn.dataset['newentry']!;

    const reply = await call({ op: 'newEntryTemplate', path: dictPath });
    if (reply.error) { alert(reply.error); return; }
    const template = reply['template'] as NodeData;

    if (btn.dataset['collection'] === 'set') {
        openSetCreateForm(btn, dictPath, template,
            (reply['candidates'] as Array<{ id: number; label: string }>) ?? []);
        return;
    }

    // Dictionary: a manual key plus the entry value.
    const form = document.createElement('form');
    form.className = 'create-form';
    form.innerHTML = createFormHtml(template);

    // open=false → Save: create and return to the list. open=true → Save & open:
    // create and navigate to the new entry.
    async function submit(open: boolean): Promise<void> {
        const value = buildValue(template, form);
        const keyInput = form.querySelector<HTMLInputElement>('input[name="__key"]');
        const res = await call({ op: 'addEntry', path: dictPath, key: keyInput?.value ?? '', value });
        if (res.error) { showCreateError(form, String(res.error)); return; }
        if (open) location.href = joinPath(dictPath, String(res['key']));
        else location.reload();
    }

    form.addEventListener('submit', e => { e.preventDefault(); void submit(false); });
    form.querySelector<HTMLButtonElement>('button[data-save]')!
        .addEventListener('click', e => { e.preventDefault(); void submit(false); });
    form.querySelector<HTMLButtonElement>('button[data-saveopen]')!
        .addEventListener('click', e => { e.preventDefault(); void submit(true); });
    form.querySelector<HTMLButtonElement>('button[data-cancel]')!
        .addEventListener('click', () => form.remove());

    (btn.parentNode ?? document.querySelector('main'))!.insertBefore(form, btn);
}

// Create form for a set: pick an existing object of the element type, or create
// a new one (minted into the extent and linked into the set).
function openSetCreateForm(
    btn: HTMLButtonElement,
    setPath: string,
    template: NodeData,
    candidates: Array<{ id: number; label: string }>,
): void {
    const form = document.createElement('form');
    form.className = 'create-form';

    let newFields = '';
    if (template.type === 'object' && template.fields)
        for (const [name, v] of Object.entries(template.fields)) {
            if (v.type === 'dictionary' || v.type === 'set' || v.type === 'object') continue;
            newFields += `<div class="field"><label>${escapeHtml(humanize(name))}</label>${inputHtml(name, v)}</div>`;
        }

    const options = candidates
        .map(c => `<option value="${c.id}">${escapeHtml(c.label)}</option>`)
        .join('');

    form.innerHTML =
        `<h3>New</h3>` +
        `<div data-ref data-current="new">` +
        `<div class="ref-toggle">` +
        `<button type="button" data-mode="existing">Use existing</button>` +
        `<button type="button" data-mode="new">Create new</button></div>` +
        `<div class="ref-existing"><select data-pick>${options}</select></div>` +
        `<div class="ref-new">${newFields}</div>` +
        `</div>` +
        `<div class="actions">` +
        `<button type="submit" data-save>Save</button>` +
        `<button type="button" data-saveopen>Save &amp; open</button>` +
        `<button type="button" data-cancel>Cancel</button></div>` +
        `<p class="error" hidden></p>`;

    form.querySelectorAll<HTMLButtonElement>('[data-mode]').forEach(modeBtn => {
        modeBtn.addEventListener('click', e => {
            e.preventDefault();
            const editor = form.querySelector<HTMLElement>('[data-ref]');
            if (editor) { editor.dataset['current'] = modeBtn.dataset['mode']!; updateRefMode(editor); }
        });
    });

    async function submit(open: boolean): Promise<void> {
        const mode = form.querySelector<HTMLElement>('[data-ref]')?.dataset['current'] ?? 'new';
        let msg: Record<string, unknown>;
        if (mode === 'existing') {
            const sel = form.querySelector<HTMLSelectElement>('select[data-pick]');
            msg = { op: 'addEntry', path: setPath, refId: Number(sel?.value) };
        } else {
            msg = { op: 'addEntry', path: setPath, value: buildValue(template, form) };
        }
        const res = await call(msg);
        if (res.error) { showCreateError(form, String(res.error)); return; }
        if (open) location.href = joinPath(setPath, String(res['key']));
        else location.reload();
    }

    form.addEventListener('submit', e => { e.preventDefault(); void submit(false); });
    form.querySelector<HTMLButtonElement>('button[data-save]')!
        .addEventListener('click', e => { e.preventDefault(); void submit(false); });
    form.querySelector<HTMLButtonElement>('button[data-saveopen]')!
        .addEventListener('click', e => { e.preventDefault(); void submit(true); });
    form.querySelector<HTMLButtonElement>('button[data-cancel]')!
        .addEventListener('click', () => form.remove());

    (btn.parentNode ?? document.querySelector('main'))!.insertBefore(form, btn);
    const editor = form.querySelector<HTMLElement>('[data-ref]');
    if (editor) updateRefMode(editor);
}

// Dictionary create form: a manual key plus the entry value (object fields, or a
// single scalar value).
function createFormHtml(template: NodeData): string {
    const keyRow = `<div class="field"><label>Key</label><input type="text" name="__key"></div>`;
    let body = '';
    if (template.type === 'object' && template.fields) {
        for (const [name, v] of Object.entries(template.fields)) {
            // Collection / reference fields are navigation boundaries — created empty
            // and edited after navigating in, so they don't belong on the create form.
            if (v.type === 'dictionary' || v.type === 'set' || v.type === 'object') continue;
            body += `<div class="field"><label>${escapeHtml(humanize(name))}</label>${inputHtml(name, v)}</div>`;
        }
    } else {
        body = `<div class="field"><label>Value</label>${inputHtml('__value', template)}</div>`;
    }
    return `<h3>New</h3>${keyRow}${body}` +
        `<div class="actions">` +
        `<button type="submit" data-save>Save</button>` +
        `<button type="button" data-saveopen>Save &amp; open</button>` +
        `<button type="button" data-cancel>Cancel</button></div>` +
        `<p class="error" hidden></p>`;
}

function inputHtml(name: string, data: NodeData): string {
    const n = escapeHtml(name);
    switch (data.type) {
        case 'bool':    return `<input type="checkbox" name="${n}"${data.value ? ' checked' : ''}>`;
        case 'int':     return `<input type="number" name="${n}" value="${data.value}">`;
        case 'decimal': return `<input type="number" step="any" name="${n}" value="${data.value}">`;
        case 'date':    return `<input type="date" name="${n}" value="${escapeHtml(data.value)}">`;
        default:        return `<input type="text" name="${n}" value="${escapeHtml(String('value' in data ? data.value : ''))}">`;
    }
}

// Build the addEntry `value` (object field-map or a single base value) from the form.
function buildValue(template: NodeData, form: HTMLFormElement): unknown {
    if (template.type === 'object' && template.fields) {
        const fields: Record<string, unknown> = {};
        for (const name of Object.keys(template.fields)) {
            const input = form.querySelector<HTMLInputElement>(`[name="${cssEscape(name)}"]`);
            if (input) fields[name] = readInput(input);
        }
        return fields;
    }
    const input = form.querySelector<HTMLInputElement>('[name="__value"]')!;
    return readInput(input);
}

function showCreateError(form: HTMLElement, message: string): void {
    const p = form.querySelector<HTMLParagraphElement>('p.error');
    if (p) { p.textContent = message; p.hidden = false; }
}

// ── helpers ─────────────────────────────────────────────────────────────────────

function joinPath(dictPath: string, key: string): string {
    return (dictPath.endsWith('/') ? dictPath : dictPath + '/') + key;
}

// "companyName" -> "Company name" (mirrors the server-side Humanize).
function humanize(name: string): string {
    const spaced = name
        .replace(/[_-]+/g, ' ')
        .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
        .toLowerCase()
        .trim();
    return spaced.length === 0 ? spaced : spaced[0].toUpperCase() + spaced.slice(1);
}

function escapeHtml(s: string): string {
    return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

function cssEscape(s: string): string {
    return s.replace(/["\\]/g, '\\$&');
}
