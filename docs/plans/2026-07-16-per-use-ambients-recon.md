# Code Context — M12 per-use ambients (NEXT rung)

Settled product decisions (from task + `docs/plans/m12-remaining.md` §3):

- Fake **`currentUser`**, **`path`**, and any other names the MetaUse author writes as ambient rows.
- Do **not** fake **`status`** (save-lifecycle / HTTP status; live page binds it writable on system scope).
- Missing ambient for a read = **error** (canvas-never-lies). Pin-flip **only** scenarios that **provide** fakes.
- Do **not** fill `sys.evalContext.ambients` as the product surface — bind fakes as **real scope root vars** like live pages do.
- Reserved-empty `evalContext.ambients` / `params` is a **naming pitfall only**.

---

## Files Retrieved

1. `DeEnv/instances/1/app.deenv` (lines 34–52, 104–111, 210, 244–260, 280–292, 823–856) — MetaUse schema, Configurations UI, `attrRow` / `addUse` / `addUseArg`, static `sys.renderTree` preview.
2. `DeEnv/Instance/workbench.ts` (lines 35–50, 222–244, 339–393, 480–567) — live instance sandbox: args → attributes, parent-less scope, `ambient: null`, remount signature.
3. `DeEnv/Http/SsrRenderer.cs` (lines 258–378, 1423–1450, 1615–1660, 1298–1301) — live `currentUser`/`path`/`status` system scope; `BuildEvalContext` payload including reserved-empty `ambients`/`params`; `LoadPrincipal`.
4. `DeEnv/Designer/SchemaBridge.cs` (lines 581–671) — `RenderExprSources` + `CollectUseArgSources` (MetaUse.args → `ctx.exprs`).
5. `DeEnv/Instance/codeExec.ts` (lines 87–138, 230–248, 1078–1120, 1457–1490, 1661–1692) — language ambient frames; `renderTree` + V1b `bindVars` root bindings; `evalCtxExpr` isolated scope (db + fns + bindings only).
6. `DeEnv.Tests/Features/DesignerComponents.feature` (lines 439–462) — pin: unseeded ambient → `"Variable currentUser not found"`.
7. `DeEnv.Tests/Features/DesignerLibrary.feature` (lines 522–549) — pin: throwing handler ambient → same error string.
8. `docs/plans/m12-remaining.md` (lines 207–241) — product goal + grounded mechanism (schema + two binding sites + pin flips).
9. `DeEnv/Code/CodeExecutor.cs` (lines 159–161, 328–334) — C# ambient push + `"Variable '{name}' not found."` (note: server message uses quotes; client omits them).

---

## Key Code

### 1. MetaUse schema + UI authoring patterns (copy for ambients)

**Schema today** (`app.deenv`):

```text
MetaAttr
    name text
    value text
    order int
MetaUse
    name text
    args set of MetaAttr
    order int
```

**Planned product shape** (m12-remaining + settled decisions):  
`MetaUse += ambients set of MetaAttr` — same MetaAttr row (name = ambient name, value = expression source text).

**Mint helpers** (exact patterns to clone):

```244:249:DeEnv/instances/1/app.deenv
    fn addUse(f)
        f.uses.add({ name: "", args: [], order: orderForAppend(f.uses) })

    fn addUseArg(u)
        u.args.add({ name: "", value: "", order: orderForAppend(u.args) })
```

Implementer should extend:

- `addUse` object literal: `args: [], ambients: []` (and any other call sites that mint MetaUse).
- `addUseAmbient(u)` twin of `addUseArg`.
- Optional: `useAmbientNameHint` (legal names? free-form is fine per settled “whatever MetaUse author writes”; still useful to warn on empty name or reserved `status` if product wants soft UX only).

**Shared attr editor** (reuse for ambient rows):

```280:292:DeEnv/instances/1/app.deenv
    fn attrRow(coll, a)
        return <div class="node-attr">
            <input class="node-attr-name" value={a.name}>
            ...
            <input class="node-attr-value" value={a.value} placeholder={"\"value\" or expression"}>
```

**Configurations card** (args loop + preview):

```823:854:DeEnv/instances/1/app.deenv
                                    <div class="fn-uses">
                                        ...
                                        foreach u in f.uses.orderBy(u => u.order)
                                            <div class="use-row">
                                                ...
                                                foreach a in u.args.orderBy(a => a.order)
                                                    attrRow(u.args, a)
                                                    ...
                                                <div class="node-add-row">
                                                    <button class="add-attr" onClick={() => addUseArg(u)}>
                                                        "+ arg"
                                                ...
                                                <section class="design-canvas use-preview" instancemount={sys.id(u)}>
                                                    sys.renderTree({ kind: "", tag: f.name, expr: "", attrs: u.args, children: [] }, sys.evalContext(design, evalRefresh), design)
```

Copy block: second foreach + `+ ambient` button under the same `use-row`. Static preview currently only passes `attrs: u.args` — ambient fakes for **static** canvas need either root-binding injection inside `renderTree` (preferred product: bind like design vars) or a workbench-only path (live instance). Product says **both** workbench sandbox and static canvas (V1b idiom).

Access rules already cover MetaUse/MetaAttr (`* where currentUser.role == "Admin"`) — new field on MetaUse does not need a new type.

---

### 2. How configuration args bind into the workbench sandbox today

Flow:

1. **Storage**: MetaUse.args = set of MetaAttr `{name, value, order}`.
2. **AST ship**: `SchemaBridge.CollectUseArgSources` → `BuildEvalContext` → `ctx.exprs[source] = {text, ast}`.
3. **Static preview**: `sys.renderTree(synthNode with attrs: u.args, ctx, design)` (app.deenv).
4. **Live mount**: `instancemount={sys.id(u)}` → `workbench.ts` `findUseRow` → `locateInstanceInputs` → `renderWorkbenchInstance`.

**Args → real component params** (`workbench.ts`):

```339:369:DeEnv/Instance/workbench.ts
function renderWorkbenchInstance(...): ... {
    const evalScope: ExecScope = { items: {}, parent: null };
    const db = instanceDb !== undefined ? instanceDb : deepCopyDbFromCtx(ctx);
    if (db != null) evalScope.items["db"] = { value: db, isReadOnly: true };
    bindFnMap(ctx.props["lib"], evalScope);
    bindCtxFns(ctx, evalScope);
    ...
    const argsV = use.props["args"];
    const argRows = ...
    const attributes: CodeTagAttribute[] = [];
    for (const a of argRows) {
        const name = propText(a, "name");
        if (name.length === 0) continue;
        const ast = lookupCtxAst(ctx, propText(a, "value"));
        if (ast != null) attributes.push({ name, value: ast });
    }
    const tag: CodeTag = { type: "tag", name: fnName, attributes, children: [] };
    const sandboxContext: ExecContext = { lastId, ambient: null };
    const view = executeComponentValue(tag, component, evalScope, sandboxContext);
```

- Args are **not** put on `evalScope` directly; they become **tag attributes**, then `executeComponentValue` / BindParams evaluates them into the **call scope** (same as live invocation).
- Missing AST → attr omitted → param binds **null** (not error).
- Remount trigger: `useArgsSignature` (name + value text + AST-availability). **Ambients must join this signature** or ambient edits will not remount.

**Current ambient gap** (explicit):

```562:567:DeEnv/Instance/workbench.ts
function instanceHandlerContext(useId: number): ExecContext {
    ...
    return { lastId: instance?.lastId ?? { value: 0 }, ambient: null };
}
```

Comments at ~563: path/currentUser/ambient reads → real `"Variable not found"` until per-use ambients land.

---

### 3. Design vars / canvas root bindings (V1b)

`sys.renderTree` seeds **rootBindings** from `design.vars` via `bindVars`:

```1109:1118:DeEnv/Instance/codeExec.ts
    let rootBindings: { [name: string]: ExecValue } | undefined = undefined;
    if (ctx != null && design != null) {
        rootBindings = {};
        bindVars(orderedMembersOptional(design, "vars", context), rootBindings, ctx, context);
    }
    const result = renderTreeNode(node, context, ctx, rootBindings, fns, ...);
```

`bindVars` (`codeExec.ts` ~1474–1490): empty init → null; collision → unbind+poison; non-empty init → `evalCtxExpr(init, ctx, bindings)` (db + fns + prior bindings); miss → leave unbound so references **chip**.

`evalCtxExpr` (`~1661–1692`): isolated parent-less scope = `db` + `bindCtxFns` + optional `bindings`; **`ambient: null`**. Canvas expression evaluation never walks language ambient frames.

**Implication for static canvas ambients:** inject evaluated ambient fakes into the **same** `rootBindings` / `evalScope.items` map (read-only), **not** into `ExecContext.ambient` and **not** into `ctx.props.ambients`.

Note: static config preview’s synth node is a **component invocation** with `attrs: u.args`. Component body evaluation for static walk goes through expandFn/param binding + bindVars for MetaFn.vars — ambient system vars like `currentUser` are still absent unless root/eval scope gets them. Live workbench uses full `executeComponentValue` (real runtime), so scope-root binding is the critical path for pin scenarios.

---

### 4. Live pages: currentUser / path shape (mimic for fakes)

`SsrRenderer.ExecuteRender` builds **system ← lib ← app** scopes:

```272:276:DeEnv/Http/SsrRenderer.cs
        system.Items["currentUser"] = new ExecScopeItem { Value = currentUser, IsReadOnly = true };
```

```375:378:DeEnv/Http/SsrRenderer.cs
        system.Items["path"] = new ExecScopeItem { Value = new ExecText { Value = urlPath }, IsReadOnly = false };
```

Also on system (do **not** fake for preview product unless explicitly desired): `status` (writable int, default 200), `anonymousLockedOut`, `accessActive`, `canManageUsers`, `db`, `sys`, `isGeneric`.

**currentUser value shape:** `AccessFloor.LoadPrincipal` — scalar-only ExecObject for the User principal, or null when anonymous (`SsrRenderer.cs` 1298–1301). Live access pins note role is **not** always shipped to client state for privacy; **designer fakes** are free to evaluate ordinary object/literal expressions (e.g. `{ role: "Admin", name: "Ada" }`) via `evalCtxExpr` — **object shape is expression-driven**, not a fixed DTO. Risk: fakes that look like full User graph vs scalar-only live principal — document as residual fidelity gap if needed.

**path type:** `ExecText` (URL path string). Fakes should evaluate to text (or accept whatever expression yields; type mismatches surface as normal runtime errors).

---

### 5. Language ambient frames vs system vars — which mechanism for per-use fakes?

| Mechanism | What it is | Used for |
|-----------|------------|----------|
| **Lexical / scope items** (`ExecScope.Items`) | Ordinary variables: `db`, `path`, `currentUser`, params, `var` | Live page system vars; design vars / row bindings on canvas; **recommended for per-use fakes** |
| **Dynamic ambient frames** (`ExecContext.ambient` / `AmbientFrame`) | `ambient name = value` statements; fallback in `executeSymbol` after scope miss | Data-context `ctx`, staging/commit overlays |
| **`ctx.props.ambients` on evalContext payload** | Reserved-empty Constant object in `BuildEvalContext` | **Not** product surface for this rung |

`executeSymbol` (client):

```230:248:DeEnv/Instance/codeExec.ts
    if (itemScope == null) {
        for (let f = context.ambient ?? null; f != null; f = f.parent)
            if (f.name === codeSymbol.name) return { value: f.value };
        throw new Error(`Variable ${codeSymbol.name} not found`);
    }
```

**Product binding mechanism for per-use fakes:** **scope root items** (read-only), mirroring SSR `system.Items["currentUser"]` / `["path"]`, and mirroring workbench’s existing `evalScope.items["db"]`.

Do **not** push `AmbientFrame` for currentUser/path unless you deliberately want dynamic-scope semantics (closures capture ambient at birth — different from system vars on top scope). Live pages put currentUser on **scope**, not ambient frames.

---

### 6. Gherkin flip targets (exact)

| File | Scenario | Assertion |
|------|----------|-----------|
| `DeEnv.Tests/Features/DesignerComponents.feature` ~447–461 | `A component reading an unseeded ambient shows the real error in its configuration card, and the page stays alive` | `configuration 0's live instance shows the error "Variable currentUser not found"` |
| `DeEnv.Tests/Features/DesignerLibrary.feature` ~531–549 | `A throwing instance handler shows the real error without disabling the page or its sibling instance` | Same error after click on Thrower’s button |

**Pin policy (settled):** keep **error** when ambient **not** provided. Flip only when scenarios **author ambient rows** with fakes. Isolation scenarios may stay as error pins (prove missing ambient still errors + page alive) **or** gain a sibling “with ambient fake shows value” scenario — do not silently change error pins to success without providing fakes.

Client error string is **`Variable currentUser not found`** (no quotes around name). C# twin uses quotes: `Variable '{name}' not found.` — designer pins match **client**.

---

### 7. SchemaBridge / evalContext.exprs for ambient expressions

**Yes — ambient expression sources need shipping in `ctx.exprs`**, same as MetaUse.args:

```616:625:DeEnv/Designer/SchemaBridge.cs
    private static void CollectUseArgSources(ObjectValue fn, List<string> into)
    {
        foreach (var use in OrderedObjects(fn.Fields.GetValueOrDefault("uses")))
            foreach (var a in OrderedObjects(use.Fields.GetValueOrDefault("args")))
                if (TextField(a, "value") is { Length: > 0 } value) into.Add(value);
    }
```

Extend with **`CollectUseAmbientSources`** (or fold into same loop over `use.Fields["ambients"]`) called from `RenderExprSources` next to `CollectUseArgSources` (~602–603).

Without this: first paint `lookupCtxAst` miss → if workbench **omits** binding (like missing arg AST → null), product would **lie** (null vs error). Settled product: missing ambient read = error. Two cases:

1. **Author provided ambient row but AST not yet shipped** — same auto-live parse-op path as args; signature should include AST-availability flag (copy `useArgsSignature`).
2. **Author did not provide ambient** — do not bind name → real Variable not found.

Do **not** put evaluated ambient values into `ctx.props["ambients"]` for workbench consumption as the main path (naming pitfall + product decision).

`BuildEvalContext` still returns:

```1615:1616:DeEnv/Http/SsrRenderer.cs
            return Obj(("db", seedDb), ("exprs", exprsObj), ("fns", fnsObj), ("types", typesObj), ("lib", libObj),
                ("libNames", libNamesArr), ("ambients", Obj()), ("params", Obj()));
```

Leave reserved-empty unless a separate rung redefines that bag.

---

### 8. Projection / import / print paths for MetaUse.ambients

| Path | Must know? | Notes |
|------|------------|--------|
| **Designer store schema** (`app.deenv` types) | **Yes** | Add field `ambients set of MetaAttr` on MetaUse. |
| **SchemaBridge.ProjectDesignDb / ProjectRenderUi** | **No for app document** | MetaUse is **designer-only**; not projected into printed `ui` Code (uses never become app document). Comments in SchemaBridge: use never reaches app document; only canvas/workbench. |
| **SchemaBridge.RenderExprSources** | **Yes** | Collect ambient value sources into exprs. |
| **Fn fingerprints** | **No** | MetaUse is not part of projected fn behavior / call-position staleness. |
| **Import render** | **No** | Import builds MetaNode/MetaFn from ui Code, not MetaUse. Configurations are designer-authored rows. |
| **DesignMerge** | **Indirect** | Merge is on printed app document / code vars, not MetaUse rows in design DB. Designer graph merge (if any) is store-level object fields — new set field rides ordinary set merge if present. |
| **DesignerSeed** | **Only if fixtures mint MetaUse** | Ensure mint literals include `ambients: []` if seed creates uses. |
| **app.deenv mint sites** | **Yes** | `addUse`, any `{ name, args, order }` literals. |
| **Tests / Gherkin steps** | **Yes if UI steps add ambient rows** | New steps: add ambient to configuration, set name/value (mirror arg steps). |

---

## Architecture (how pieces connect)

```
MetaUse (design DB)
  args: MetaAttr[]     ──► CollectUseArgSources ──► ctx.exprs
  ambients: MetaAttr[] ──► CollectUseAmbientSources ──► ctx.exprs  (TO ADD)
       │
       ├─ static: sys.renderTree({tag: fn, attrs: args}, evalContext, design)
       │            └─ rootBindings = design.vars (+ TO ADD: evaluated use.ambients at workbench static path?)
       │            └─ expandFn / evalCtxExpr: db + fns + bindings
       │
       └─ live: instancemount → workbench.renderWorkbenchInstance
                  evalScope: db, lib, fns, (+ TO ADD: ambient fakes as items)
                  args → CodeTag attributes → executeComponentValue
                  ExecContext.ambient stays null (unless data-context inside component)
```

Two binding sites (from plan + code):

1. **`workbench.ts` `renderWorkbenchInstance`** (+ handler context if handlers re-read scope; handlers use same component closures — root scope items captured at setup are what matter).
2. **Static canvas for config card** — today `renderTree` does not receive the MetaUse row; only synth attrs. Options:
   - **A (minimal live-only):** only workbench binds ambients (pins are live-instance). Static tree may still chip/miss ambient reads in non-expanded display paths — config preview is mostly live after mount (static body replaced on mount).
   - **B (full product):** pass ambients into renderTree or bind before expand when walking a use-preview; V1b rootBindings extension keyed by use is awkward because renderTree doesn’t know use id.
   
**Practical note:** U1 static `sys.renderTree` body is **replaced on first mount** by workbench (`ensureInstanceContent` clears SSR pre-mount body). Pin scenarios assert **live instance** errors/UI. **Primary implement site = workbench scope binding.** Static path is secondary unless unmounted SSR preview must also fake ambients.

---

## Binding-site pseudocode sketch

```ts
// workbench.ts — inside renderWorkbenchInstance, AFTER db/lib/fns, BEFORE executeComponentValue:

function bindUseAmbients(use: ExecObject, ctx: ExecObject, evalScope: ExecScope): void {
  const rows = orderMetaAttrs(use.props["ambients"]);
  for (const a of rows) {
    const name = propText(a, "name");
    if (!name) continue;
    // Product: do not auto-bind status even if authored? Soft-skip or bind — settled: do NOT fake status
    // as a first-class ambient; if author names it "status", either refuse or bind as ordinary fake —
    // recommend: bind any name (author freedom) but docs say don't use status; optional UX hint.
    const src = propText(a, "value");
    if (!src) continue; // no expression → do not bind → read still errors (canvas-never-lies)
    const value = evalCtxExpr(src, ctx, /*bindings*/ undefined);
    // evalCtxExpr returns null on miss/throw — do not bind null-as-fake for "miss"; leave unbound
    // so Variable not found. If author wrote literal null and AST exists, value is ExecNull — OK bind.
    if (value != null)
      evalScope.items[name] = { value, isReadOnly: true };
  }
}

// Remount signature (extend useArgsSignature):
//   ... existing args ... + ambient rows (name, value, ast flag) same length-prefix idiom

// SchemaBridge:
//   CollectUseArgSources → also ambients set
//   RenderExprSources already calls CollectUseArgSources per fn — extend that helper

// app.deenv:
//   MetaUse.ambients set of MetaAttr
//   addUse: ambients: []
//   addUseAmbient + UI foreach attrRow(u.ambients, a)
```

**evalCtxExpr for ambient values:** same isolation as args (db + design fns). That lets fakes use `db.…` seed data. It does **not** include real session currentUser (correct for designer).

---

## Start here (ordered file list for implementer)

1. **`DeEnv/instances/1/app.deenv`** — schema field + mint + Configurations UI (pattern exists at uses/args).
2. **`DeEnv/Designer/SchemaBridge.cs`** — `CollectUseArgSources` / `RenderExprSources` ambient values → exprs.
3. **`DeEnv/Instance/workbench.ts`** — bind ambients onto sandbox `evalScope`; extend `useArgsSignature`.
4. **`DeEnv.Tests/Features/DesignerComponents.feature`** (+ steps if needed) — new happy-path with authored ambient fakes; keep isolation error pin when unset.
5. **`DeEnv.Tests/Features/DesignerLibrary.feature`** — same policy for handler throw pin.
6. Optionally twin C# canvas if any server-side renderTree path must match (primary pins are client workbench).

---

## Risks & open questions

| Risk | Detail |
|------|--------|
| **Name collision: `evalContext.ambients`** | Payload still ships empty `ambients` object. Code that does `ctx.props.ambients` for product will confuse reserved bag with MetaUse.ambients. Bind from **use row**, never from ctx bag. |
| **currentUser object shape** | Live = LoadPrincipal scalar object or null. Fake = any expr result. `{ role: "Admin" }` is enough for typical component reads; full User id/password not required. |
| **path type** | Live = text URL path. Fake expr should yield text for `sys.segment(path, n)` etc. |
| **status** | Do not present as first-class fakeable ambient in UX copy; binding if authored is optional policy. |
| **Arg vs ambient null semantics** | Missing arg AST → null param. Missing ambient → unbound → error. Do not unify. |
| **Signature / remount** | Ambient edit without signature update = stuck preview. |
| **Collision with fn params / db / lib names** | Binding ambient after lib/fns: same-name ambient overwrites scope item if written later — define order (recommend after lib/fns, like system vars sit under call scope; params still on call child scope). |
| **Language ambient vs scope** | Using AmbientFrame would interact with `capturedAmbient` on closures differently than live pages. Prefer scope items. |
| **Handler path** | Setup captures scope; rebinding only on remount is correct. Signature must remount on ambient edit. |
| **Tests** | Error string client-specific; steps may need “add ambient to configuration N”. |

---

## Supervisor coordination

None required — product decisions were settled in the task brief; recon is complete.
