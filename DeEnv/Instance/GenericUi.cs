using DeEnv.Code;

namespace DeEnv.Instance;

// The self-hosted generic UI (milestone 9), a reflective Code library over schema data,
// synthesized into the effective ui at render time. It is the DEFAULT UI: any app without a
// fully-custom `fn render()` (including an app with no `ui` section) renders through it. The
// library is also PUBLIC (milestone 11): its components are named in PascalCase and reachable
// from a custom `fn render()` — the renderer parents the app scope under the library scope, so
// `<ObjectForm …>` &c. resolve by name and a hand-written render can compose them.
//
//   • Input(obj, desc, variant) — the baseType-appropriate, two-way-bound control for one prop (the
//     desc): <input type={InputType(...)}> for a scalar, a checkbox for bool, a <select> of values
//     for an enum, and a <textarea> for a multiline text prop. The first composition PRIMITIVE —
//     extracted from the three places that inlined the
//     same baseType branch (the object-form field, the reference create-new draft, the set add-form).
//     `variant` is an optional MUI-style presentation choice OWNED by the library (callers never
//     restyle via their own CSS): omitted → "outlined" (bordered, the form default); "standard" → a
//     borderless control that reads as plain text and reveals an underline on hover/focus (for inline
//     contexts like a checklist row). The only thing a caller passes for looks; styling is internal.
//     (A multiline text prop renders a <textarea>, which ignores `variant` — no styled variant exists
//     for it yet; add one only if a caller needs an inline multiline control.)
//   • Field(obj, desc) — a labeled field: a <div class="field"> wrapping the prop's humanized
//     <label> and its Input. The labeled-field composite ObjectForm and a custom render compose.
//   • ObjectForm(obj, meta, base, autosave, join, body, onSave, onCancel) — EITHER an object page
//     (edit-mode, no `join`) OR a create form over a draft (create-mode, `join` given). Edit-mode:
//     a field per prop (a Field for a scalar, a nested RefEditor for a single object reference, an
//     inline SetTable for an object set, or an inline DictTable for a dictionary). A collection's
//     label is a navigable list-title link to its own route. `base` is the page's URL path, so inline
//     links nest. A COMPONENT: it opens a data-context — `ambient ctx = ctx.new(live)` — and binds Fields
//     to the LIVE `obj` directly; the ctx decides whether a write stages or persists. The ctx is LIVE
//     when `autosave` is true OR — in EDIT mode — the form renders NO scalar fields (so it has no Save to
//     commit on: the Db root, a scalar-less container). This aligns "has a Save" with "is a staging
//     transaction" (atomic-commit Step B): a create under a Save-less container persists IMMEDIATELY
//     (nothing to defer to), never trapped in a context that can never commit. Otherwise (the common
//     edit page) the ctx is a STAGING child: scalar edits stage in the overlay (the stored object is
//     untouched) and commit on a Save button (`ctx.commit()`); Discard drops the overlay
//     (`ctx.discard()`). A LIVE ctx (`autosave={true}`) → per-keystroke autosave, no buttons. COLLECTION
//     props (reference/set/dictionary) bind to the LIVE object and each manages its own members.
//       Create-mode (B1 collapse): when `join(obj)` is given, ObjectForm renders the `.create-form` card
//     — a scalar-only field list (or the supplied `body(draft)`) + a Save that does `join(obj)` (then the
//     caller's `onSave` to close). This is the SINGLE create path the SetTable and RefEditor both reveal (a
//     nested create-mode ObjectForm over a `sys.new` draft). `join(obj)` does `set.add(draft)` /
//     `setRef(…, draft)`, which AUTO-DEFERS (atomic-commit Step B): a transient draft added under a STAGING
//     ctx STAGES into that ctx's creates instead of firing live. The relevant staging ctx is the one the
//     `join` CLOSURE captured — the ENCLOSING SetTable/RefEditor's ambient — so a generic create under an
//     object's form stages into the OBJECT FORM's ctx and persists on its Save (`ctx.commit` flushes
//     creates). Under a Save-less container (a top-level set route) that ambient ctx is LIVE, so the create
//     persists immediately on Add. (The create-mode form's OWN ctx therefore stays empty for these flows —
//     the draft's scalar edits write to its live `obj` directly (id<0 bypasses staging) and the create joins
//     the enclosing ctx — so no inner `ctx.commit()` is needed; the deferral is entirely the staging branch
//     + the enclosing Save.) `join` + `body` are PERMANENT params; `onSave`/`onCancel` close the CALLER's
//     draft slot (SetTable/RefEditor owns the draft + open/close toggle), so the form calls back after Add.
//   • RefEditor(parent, prop, target) — a reference editor: current label, a pick button
//     per extent() candidate, a clear button, and a create-new form. A COMPONENT: its body
//     runs once as init (a local `state` holding a draft), and it returns a render fn.
//   • RefSelect(parent, prop, candidates, labelProp) — a generic ref-binding <select>: the
//     bare picker, no buttons. The select binds a HIDDEN scalar `state.pick` (the chosen
//     candidate's id, seeded from the parent's current ref); on a native change its `onChange`
//     handler `applyPick` does `sys.setRef(parent, prop, candidates.single(c => sys.id(c) == state.pick))`
//     — the write is in HANDLER position (both twins agree: client stages the draft, server no-ops),
//     never in render. A "(choose…)" pick (id 0) makes `single(…)` return null → the ref CLEARS. The
//     candidate list is the caller's `candidates` collection (not necessarily the full extent), labeled
//     by `labelProp`. Use it where a form needs to bind ONE reference from a known candidate set without
//     the full RefEditor (current-label / per-candidate buttons / create-new).
//   • ImageInput(obj, prop) — the ONE upload primitive (assets-design.md): an `<input type="file">`
//     two-way-bound to `sys.field(obj, prop)` like any other input, but its bind is handled SPECIALLY
//     client-side (ui.ts wireEvents/refreshAttributes): picking a file POSTs it to the instance's blob
//     pool (ws.ts uploadBlob) and, once that resolves, writes the returned content-hash NAME back
//     through the SAME setValue the binding already carries — so the persist (staging/history/wire) is
//     byte-identical to any other bound scalar edit. A PUBLIC library component (the RefSelect
//     precedent): the generic Input()'s "image" branch composes it (thumbnail + this + a Clear
//     button), and a custom `fn render()` can compose it directly the same way.
//   • SetTable(set, desc, setPath, columns, rowActions, createForm, onCreate, linked) — a set table: an aligned header +
//     member rows + a `+ New` button. A whole data row is navigable — its first cell wraps the member's
//     identity (labelProp value) in a stretched `<a class="row-link" href=nest(setPath, m)>` (CSS
//     `::after { inset:0 }` covers the row); a per-row Remove sits z-raised above the overlay. A bool
//     column renders a read-only ✓/✗ glyph, never "true"/"false". Shared structure with DictTable
//     (row-link + Remove + bool cell + the `+ New`/create-form pattern), differing only in the identity
//     source. A COMPONENT: the read-only table ALWAYS renders; a local `state.creating` flag toggles what
//     sits BELOW it — the `+ New` button (default) or a labeled CREATE FORM with Save / Cancel. So the
//     existing list stays visible while you add a member (a tracker keeps the list in view while
//     appending); the create form is a separate card under the table, hidden until asked — NOT an inline
//     add row (tables stay read-only; edits happen on the member page). Save commits the draft (`set.add`)
//     and closes the form; Cancel discards it; both reset `state.draft`. The last three params shape the table (all optional, default
//     to the plain data-table behavior):
//       · columns — the column names to show (omitted/null → labelProp + every scalar prop).
//       · rowActions(m) — a per-row action cell; when given, it REPLACES the default Remove cell AND
//         marks the table `managed`, which suppresses the whole-row click overlay (the stretched
//         a.row-link::after) so the consumer's action buttons are not under a click-stealing layer — the
//         label stays an in-cell nav link. Omitted/null → the default Remove cell + whole-row overlay
//         (the data-table path, byte-for-byte unchanged).
//       · createForm(draft) — the body of the create form: a function returning the fields to edit on the
//         create draft, rendered INSIDE the create-form card in place of the default per-scalar `Field`
//         form (the Save/Cancel + set.add flow stays around it). Omitted/null → the default all-scalars
//         form (one `Field` per scalar prop), byte-for-byte unchanged. A consumer passes it to show a
//         FOCUSED create (the designer's designs list shows just a label field, not Design's raw
//         ui/common/initialData scalars) while still using the generic New + set.add.
//       · onCreate(draft) — overrides what Save DOES with the finished draft: when given, Save calls
//         `onCreate(draft)` INSTEAD of `set.add(draft)`. For a HOST-MANAGED set (a set whose membership is
//         owned by a kernel action, not direct desired-state writes) — e.g. the designer's instances list,
//         whose rows are created by `sys.create(design, name)`, not by adding a ghost Instance row that has
//         no runtime. Omitted/null → the default `set.add(draft)`, byte-for-byte unchanged. The draft is a
//         fully-built `sys.new(desc)` object the body edited, so onCreate reads its props (e.g. draft.name,
//         draft.design) to drive the host action.
//       · linked — omitted/default (not `false`) keeps the identity cell a navigating `<a>`; `linked={false}`
//         renders it as a plain `<span>` (no `sys.nest(setPath, m)` link) for a set with no per-member route.
//
// Builtins do the reflective work, all under the framework `sys` namespace: sys.field (dynamic
// access), sys.humanize (labels), sys.extent (a type's objects), sys.schema (a type's descriptor),
// sys.setRef (set/clear a reference), sys.nest (a URL path-join for nested member links), sys.new (a
// fresh default-valued object built from a descriptor — a create-new draft). `obj.prop = x` resets a component's draft after Create.
//
//   • LoginForm() (M-auth login UI) — the auto-mode login gate: a COMPONENT whose run-once setup mints a
//     transient `state` (name + password — a negative-id object, so edits stay client-local and never
//     persist), returning two bound inputs + a Submit that calls sys.login(state.name, state.password). The
//     synthesized render returns it (`<LoginForm>`) when `anonymousLockedOut && currentUser == null`, so an
//     app where anonymous can read nothing shows login instead of an empty page. `sys.login(name, password)`
//     is a CLIENT-only host effect (a server no-op like sys.publish): on the client it sends a `login` WS op
//     whose reply drives a refetch, so the page re-renders as the bound principal (currentUser flips). The
//     boundary lives UNDER it (the WS bind + the floor); relocating/restyling login cannot weaken it.
//   • ConflictBar() (M13 slice-6 data conflicts, fine per-field UI B5) — a zero-arg COMPONENT reading the
//     ambient `ctx.conflicts` (the same-field collisions from the last rejected commit, each carrying
//     object/typeName/field/base/mine/theirs). Groups conflicts by object (labeled `TYPE #id`) and, per
//     field, shows MINE vs THEIRS inline (so the operator chooses INFORMED, not blind) with a per-field
//     Keep-mine / Take-theirs pair (`ctx.resolveField`) + whole-bar "Keep all mine" / "Take all theirs"
//     shortcuts (`ctx.keepMine`/`ctx.takeTheirs`). ObjectForm renders it automatically; a custom `fn render()`
//     composes `<ConflictBar>` to get the same surface. Client-only (the ctx.status precedent — C# twins are
//     fixed empty constants; a conflict is a WS-reply phenomenon the server never witnesses). A custom render
//     that renders NEITHER a ConflictBar nor `ctx.conflicts` still cannot silently clobber: the global error
//     banner (uiStatic.lastError) is the unconditional fallback.
//
// A type's descriptor — { name, labelProp, props } — is fetched by
// `sys.schema(typeName)`,
// resolved server-side from the schema (the descriptor literal GenericUi threads into the executor)
// and shipped to the client like extent. Components are tag-invoked and slot-keyed, so a descriptor
// is now plain argument data (its identity carries no reactivity). Cross-type references are stored
// by type NAME (cycle-safe); a component resolves them with sys.schema(name).
//
// Synthesis is render-time only: the canonical InstanceDescription (what AppPrint emits) carries no
// UI at all for a plain app. For a generic-UI app the framework synthesizes a single `fn render()`
// (below) that routes every URL by calling `sys.resolve(path)` — the Code twin of the old C#
// per-URL dispatch — and composing the library component for the resolved kind. There are no
// per-type views anymore: every page runs through this one render, exactly as a custom render does.
public static class GenericUi
{
    private const string StdlibSource = """
        ui
            fn render()
                if anonymousLockedOut && currentUser == null
                    return <LoginForm>
                var view = route()
                if currentUser != null
                    return <div class="app-shell">
                        <UserMenu>
                        view
                if accessActive
                    return <div class="app-shell">
                        <SignInBar>
                        view
                return view

            fn route()
                var r = sys.resolve(path)
                if r.kind == "object" && r.target != null
                    return <ObjectForm obj={r.target} meta={sys.schema(r.typeName)} base={path}>
                else if r.kind == "set" && sys.canRead(r.typeName)
                    return <SetTable set={sys.field(r.parent, r.prop)} desc={sys.schema(r.typeName)} setPath={path}>
                else if r.kind == "ref" && sys.canRead(r.typeName)
                    return <RefEditor parent={r.parent} prop={r.prop} target={sys.schema(r.typeName)}>
                else if r.kind == "dict"
                    return <DictTable dict={sys.field(r.parent, r.prop)} desc={sys.schema(r.parentType, r.prop)} base={path}>
                else if r.kind == "leaf" && r.target != null
                    return <LeafForm entry={r.target} base={path}>
                else
                    status = 404
                    return NotFoundForm()

            fn Input(obj, desc, variant, readonly)
                if desc.baseType == "bool"
                    return <input type="checkbox" class={desc.name} checked={sys.field(obj, desc.name)} disabled={readonly}>
                else if desc.baseType == "password"
                    return <input type="password" class={desc.name} value={sys.field(obj, desc.name)} readonly={readonly}>
                else if desc.baseType == "image"
                    return <div class={"image-field " + desc.name}>
                        if sys.field(obj, desc.name) != ""
                            <img class="image-thumb" alt="image unavailable" src={sys.assetUrl(sys.field(obj, desc.name))}>
                        if readonly != true
                            <ImageInput obj={obj} prop={desc.name}>
                            if sys.field(obj, desc.name) != ""
                                <button class="image-clear" onClick={() => sys.setField(obj, desc.name, "")}>
                                    "Clear"
                else if desc.baseType == "enum"
                    return <select class={desc.name} value={sys.field(obj, desc.name)} disabled={readonly}>
                        <option value="">
                            "(none)"
                        foreach v in desc.values
                            <option value={v}>
                                sys.humanize(v)
                else if desc.baseType == "text"
                    if desc.multiline
                        return <textarea class={desc.name} rows="4" value={sys.field(obj, desc.name)} readonly={readonly}>
                    else if variant == "standard"
                        return <input type="text" class={desc.name} value={sys.field(obj, desc.name)} variant="standard" readonly={readonly}>
                    else
                        return <input type="text" class={desc.name} value={sys.field(obj, desc.name)} readonly={readonly}>
                else if variant == "standard"
                    return <input type={InputType(desc.baseType)} class={desc.name} value={sys.field(obj, desc.name)} variant="standard" readonly={readonly}>
                else
                    return <input type={InputType(desc.baseType)} class={desc.name} value={sys.field(obj, desc.name)} readonly={readonly}>

            fn Field(obj, desc, readonly)
                return <div class="field">
                    <label class={desc.name}>
                        sys.humanize(desc.name)
                    <Input obj={obj} desc={desc} readonly={readonly}>

            fn ConfirmButton(label, onConfirm, cls)
                var state = { confirming: false }
                fn doConfirm()
                    onConfirm()
                    state.confirming = false
                fn render()
                    return <span class="confirm-button">
                        if state.confirming
                            <span class="delete-confirm">
                                label
                                "?"
                            <button class="delete-yes" onClick={doConfirm}>
                                "Yes"
                            <button class="delete-cancel" onClick={() => state.confirming = false}>
                                "Cancel"
                        else
                            <button class={cls} onClick={() => state.confirming = true}>
                                label
                return render

            fn KebabMenu(body)
                var state = { open: false }
                fn close()
                    state.open = false
                fn render()
                    return <div class="kebab">
                        <button class="kebab-toggle" onClick={() => state.open = state.open == false}>
                            "⋯"
                        <div class={state.open ? "kebab-backdrop open" : "kebab-backdrop"} onClick={close}>
                        <div class={state.open ? "kebab-menu open" : "kebab-menu"}>
                            body(close)
                return render

            fn ConflictBar()
                fn keepAll()
                    ctx.keepMine()
                fn takeAll()
                    ctx.takeTheirs()
                fn resolve(object, field, take)
                    ctx.resolveField(object, field, take)
                fn render()
                    return <div class="conflict-bar">
                        <span class="conflict-message">
                            "Someone else changed this while you were editing. Review each field — your draft still holds your values."
                        foreach c in ctx.conflicts
                            if ctx.conflicts.single(g => g.object == c.object).field == c.field
                                <div class="conflict-group">
                                    <div class="conflict-group-label">
                                        sys.humanize(c.typeName)
                                        " #"
                                        c.object
                                    foreach f in ctx.conflicts.where(g => g.object == c.object)
                                        <div class="conflict-field-row">
                                            <span class="conflict-field-name">
                                                sys.humanize(f.field)
                                            <div class="conflict-sides">
                                                <div class="conflict-mine">
                                                    <span class="conflict-side-label">
                                                        "Yours"
                                                    if f.mine == null
                                                        <span class="conflict-empty">
                                                            "(empty)"
                                                    else
                                                        <span class="conflict-val">
                                                            f.mine
                                                <div class="conflict-theirs">
                                                    <span class="conflict-side-label">
                                                        "Theirs"
                                                    if f.theirs == null
                                                        <span class="conflict-empty">
                                                            "(empty)"
                                                    else
                                                        <span class="conflict-val">
                                                            f.theirs
                                            <div class="conflict-field-actions">
                                                <button class="conflict-field-keep" onClick={() => resolve(f.object, f.field, false)}>
                                                    "Keep mine"
                                                <button class="conflict-field-take" onClick={() => resolve(f.object, f.field, true)}>
                                                    "Take theirs"
                        <div class="conflict-actions">
                            <button class="conflict-keep" onClick={keepAll}>
                                "Keep all mine"
                            <button class="conflict-take" onClick={takeAll}>
                                "Take all theirs"
                return render

            fn ObjectForm(obj, meta, base, autosave, join, body, onSave, onCancel)
                var live = autosave == true || (join == null && !meta.props.any(p => p.baseType != "object" && p.baseType != "set" && p.baseType != "dictionary"))
                ambient ctx = ctx.new(live)
                fn save()
                    if join != null
                        join(obj)
                        if onSave != null
                            onSave()
                    else
                        ctx.commit()
                fn discard()
                    if onCancel != null
                        onCancel()
                    else
                        ctx.discard()
                fn render()
                    var canEdit = sys.canWrite(meta.name, "edit")
                    var hasFields = meta.props.any(p => p.baseType != "object" && p.baseType != "set" && p.baseType != "dictionary")
                    if join != null
                        return <div class="create-form">
                            <h3>
                                "New "
                                sys.humanize(meta.name)
                            if body != null
                                body(obj)
                            else
                                foreach p in meta.props
                                    if p.baseType != "object" && p.baseType != "set" && p.baseType != "dictionary"
                                        <Field obj={obj} desc={p}>
                            <div class="create-actions">
                                <button class="create-save" onClick={save}>
                                    "Save"
                                if onCancel != null
                                    <button class="cancel" onClick={discard}>
                                        "Cancel"
                    else
                        return <div class="object-form">
                            <h2>
                                meta.name
                            if ctx.conflicts.any(c => true)
                                <ConflictBar>
                            foreach p in meta.props
                                if p.baseType == "object"
                                    if sys.canRead(p.target)
                                        <div class="field">
                                            <label class={p.name}>
                                                sys.humanize(p.name)
                                            <RefEditor parent={obj} prop={p.name} target={sys.schema(p.target)}>
                                else if p.baseType == "set"
                                    if sys.canRead(p.element)
                                        <div class="field">
                                            if isGeneric
                                                <a class="list-title" href={sys.nest(base, p.name)}>
                                                    sys.humanize(p.name)
                                            else
                                                <span class="list-title">
                                                    sys.humanize(p.name)
                                            <SetTable set={sys.field(obj, p.name)} desc={sys.schema(p.element)} setPath={sys.nest(base, p.name)} linked={isGeneric}>
                                else if p.baseType == "dictionary"
                                    <div class="field">
                                        if isGeneric
                                            <a class="list-title" href={sys.nest(base, p.name)}>
                                                sys.humanize(p.name)
                                        else
                                            <span class="list-title">
                                                sys.humanize(p.name)
                                        <DictTable dict={sys.field(obj, p.name)} desc={p} base={sys.nest(base, p.name)} linked={isGeneric}>
                                else
                                    <Field obj={obj} desc={p} readonly={!canEdit}>
                            if autosave != true && canEdit && hasFields && !ctx.conflicts.any(c => true)
                                <div class="form-actions">
                                    <button class="save" onClick={save}>
                                        "Save"
                                    <button class="discard" onClick={discard}>
                                        "Discard"
                                    <span class="save-status">
                                        if ctx.dirty != true
                                            if ctx.status == "saving"
                                                "Saving…"
                                            else if ctx.status == "saved"
                                                "Saved"
                                            else if ctx.status == "updated"
                                                "Updated to latest"
                return render

            fn RefEditor(parent, prop, target)
                var state = { pick: 0, draft: sys.new(target), creating: false }
                fn startCreate()
                    state.creating = true
                fn closeCreate()
                    state.draft = sys.new(target)
                    state.creating = false
                fn render()
                    return <div class="ref-editor">
                        <h3 class="ref-type">
                            target.name
                        <div class="ref-current">
                            "Current: "
                            if sys.field(parent, prop) == null
                                "(none)"
                            else
                                sys.field(sys.field(parent, prop), target.labelProp)
                        <div class="ref-controls">
                            <select class="ref-pick" value={state.pick}>
                                <option value="0">
                                    "(choose…)"
                                foreach c in sys.extent(target.name)
                                    <option value={sys.id(c)}>
                                        sys.field(c, target.labelProp)
                            foreach c in sys.extent(target.name)
                                if sys.id(c) == state.pick
                                    <button class="ref-set" onClick={() => sys.setRef(parent, prop, c)}>
                                        "Set"
                            <button class="ref-clear" onClick={() => sys.setRef(parent, prop, null)}>
                                "Clear"
                        if state.creating
                            <ObjectForm obj={state.draft} meta={target} join={d => sys.setRef(parent, prop, d)} onSave={closeCreate} onCancel={closeCreate}>
                        else if sys.canWrite(target.name, "create")
                            <button class="new-btn" onClick={startCreate}>
                                "New "
                                sys.humanize(target.name)
                return render

            fn RefSelect(parent, prop, candidates, labelProp)
                var state = { pick: sys.field(parent, prop) != null ? sys.id(sys.field(parent, prop)) : 0 }
                fn applyPick()
                    sys.setRef(parent, prop, candidates.single(c => sys.id(c) == state.pick))
                fn render()
                    return <select class="ref-select" value={state.pick} onChange={applyPick}>
                        <option value="0">
                            "(choose…)"
                        foreach c in candidates
                            <option value={sys.id(c)}>
                                sys.field(c, labelProp)
                return render

            fn ImageInput(obj, prop)
                return <input type="file" accept="image/png,image/jpeg,image/gif,image/webp" value={sys.field(obj, prop)}>

            fn SetTable(set, desc, setPath, columns, rowActions, createForm, onCreate, linked)
                var state = { draft: sys.new(desc), creating: false }
                fn startCreate()
                    state.creating = true
                fn closeCreate()
                    state.draft = sys.new(desc)
                    state.creating = false
                fn render()
                    var tableClass = "set-table"
                    if rowActions != null
                        tableClass = "set-table managed"
                    return <div class={tableClass}>
                        <table>
                            <tr class="set-head">
                                if columns != null
                                    foreach name in columns
                                        foreach p in desc.props.where(c => c.name == name)
                                            <th>
                                                sys.humanize(p.name)
                                else
                                    <th>
                                        sys.humanize(desc.labelProp)
                                    foreach p in desc.props
                                        if p.baseType != "set" && p.baseType != "dictionary" && p.baseType != "password" && p.name != desc.labelProp && p.multiline != true
                                            <th>
                                                sys.humanize(p.name)
                                if rowActions != null || sys.canWrite(desc.name, "delete")
                                    <th>
                            foreach m in set
                                <tr class="set-row">
                                    if columns != null
                                        foreach name in columns
                                            foreach p in desc.props.where(c => c.name == name)
                                                if p.name == desc.labelProp
                                                    <td class="row-id">
                                                        if linked != false
                                                            <a class="row-link" href={sys.nest(setPath, m)}>
                                                                sys.field(m, p.name) == "" ? "(no " + sys.humanize(p.name) + ")" : sys.field(m, p.name)
                                                        else
                                                            <span class="row-link">
                                                                sys.field(m, p.name)
                                                else
                                                    <td>
                                                        if p.baseType == "bool"
                                                            <span class="bool-cell">
                                                                boolGlyph(sys.field(m, p.name))
                                                        else if p.baseType == "object"
                                                            if sys.field(m, p.name) != null
                                                                sys.field(sys.field(m, p.name), sys.schema(p.target).labelProp)
                                                        else if p.baseType == "enum"
                                                            sys.humanize(sys.field(m, p.name))
                                                        else if p.baseType == "image"
                                                            if sys.field(m, p.name) != ""
                                                                <img class="thumb-cell" alt="image unavailable" src={sys.assetUrl(sys.field(m, p.name))}>
                                                        else
                                                            sys.field(m, p.name)
                                    else
                                        <td class="row-id">
                                            if linked != false
                                                <a class="row-link" href={sys.nest(setPath, m)}>
                                                    sys.field(m, desc.labelProp) == "" ? "(no " + sys.humanize(desc.labelProp) + ")" : sys.field(m, desc.labelProp)
                                            else
                                                <span class="row-link">
                                                    sys.field(m, desc.labelProp)
                                        foreach p in desc.props
                                            if p.baseType != "set" && p.baseType != "dictionary" && p.baseType != "password" && p.name != desc.labelProp && p.multiline != true
                                                <td>
                                                    if p.baseType == "bool"
                                                        <span class="bool-cell">
                                                            boolGlyph(sys.field(m, p.name))
                                                    else if p.baseType == "object"
                                                        if sys.field(m, p.name) != null
                                                            sys.field(sys.field(m, p.name), sys.schema(p.target).labelProp)
                                                    else if p.baseType == "enum"
                                                        sys.humanize(sys.field(m, p.name))
                                                    else if p.baseType == "image"
                                                        if sys.field(m, p.name) != ""
                                                            <img class="thumb-cell" alt="image unavailable" src={sys.assetUrl(sys.field(m, p.name))}>
                                                    else
                                                        sys.field(m, p.name)
                                    if rowActions != null
                                        rowActions(m)
                                    else if sys.canWrite(desc.name, "delete")
                                        <td class="row-action">
                                            <button class="set-remove" onClick={() => set.remove(m)}>
                                                "Remove"
                        if !set.any(m => true)
                            <p class="set-empty">
                                "No "
                                sys.humanize(desc.name)
                                " yet"
                        if state.creating
                            if onCreate != null
                                <ObjectForm obj={state.draft} meta={desc} join={d => onCreate(d)} body={createForm} onSave={closeCreate} onCancel={closeCreate}>
                            else
                                <ObjectForm obj={state.draft} meta={desc} join={d => set.add(d)} body={createForm} onSave={closeCreate} onCancel={closeCreate}>
                        else if sys.canWrite(desc.name, "create")
                            <button class="new-btn" onClick={startCreate}>
                                "New "
                                sys.humanize(desc.name)
                return render

            fn DictTable(dict, desc, base, linked)
                var state = { key: "", draft: sys.new(desc), error: "", creating: false }
                fn save()
                    if state.key == ""
                        state.error = "Key is required"
                    else if dict.any(m => sys.field(m, "__key") == state.key)
                        state.error = "Key already exists"
                    else
                        if desc.isScalar
                            dict.setEntry(state.key, sys.field(state.draft, "value"))
                        else
                            dict.setEntry(state.key, state.draft)
                        state.key = ""
                        state.draft = sys.new(desc)
                        state.error = ""
                        state.creating = false
                fn startCreate()
                    state.creating = true
                fn cancel()
                    state.key = ""
                    state.draft = sys.new(desc)
                    state.error = ""
                    state.creating = false
                fn render()
                    return <div class="dict-table">
                        <table>
                            <tr class="dict-head">
                                <th>
                                    "Key"
                                foreach p in desc.valueProps
                                    <th>
                                        sys.humanize(p.name)
                                if desc.isScalar
                                    <th>
                                        "Value"
                                <th>
                            foreach m in dict
                                <tr class="dict-row">
                                    <td class="row-id">
                                        if linked != false
                                            <a class="row-link" href={sys.nest(base, sys.field(m, "__key"))}>
                                                sys.field(m, "__key")
                                        else
                                            <span class="row-link">
                                                sys.field(m, "__key")
                                    foreach p in desc.valueProps
                                        <td>
                                            if p.baseType == "bool"
                                                <span class="bool-cell">
                                                    boolGlyph(sys.field(m, p.name))
                                            else if p.baseType == "enum"
                                                sys.humanize(sys.field(m, p.name))
                                            else if p.baseType == "image"
                                                if sys.field(m, p.name) != ""
                                                    <img class="thumb-cell" alt="image unavailable" src={sys.assetUrl(sys.field(m, p.name))}>
                                            else
                                                sys.field(m, p.name)
                                    if desc.isScalar
                                        <td>
                                            sys.field(m, "value")
                                    <td class="row-action">
                                        <button class="dict-remove" onClick={() => dict.remove(m)}>
                                            "Remove"
                        if !dict.any(m => true)
                            <p class="dict-empty">
                                "No "
                                sys.humanize(desc.name)
                                " yet"
                        if state.creating
                            <div class="create-form">
                                <h3>
                                    "New "
                                    sys.humanize(desc.name)
                                <div class="field">
                                    <label class="dict-key">
                                        "Key"
                                    <input class="dict-key" value={state.key}>
                                foreach p in desc.valueProps
                                    <Field obj={state.draft} desc={p}>
                                if desc.isScalar
                                    <div class="field">
                                        <label class="value">
                                            "Value"
                                        <input type={InputType(desc.element)} class="value" value={sys.field(state.draft, "value")}>
                                <div class="create-actions">
                                    <button class="dict-add" onClick={save}>
                                        "Save"
                                    <button class="cancel" onClick={cancel}>
                                        "Cancel"
                                <div class="dict-error">
                                    state.error
                        else
                            <button class="new-btn" onClick={startCreate}>
                                "New "
                                sys.humanize(desc.name)
                return render

            fn LeafForm(entry, base)
                return <div class="leaf-form">
                    <h2>
                        sys.field(entry, "__key")
                    <input class="value" value={sys.field(entry, "value")}>

            fn NotFoundForm()
                status = 404
                return <main class="not-found">
                    <h1>
                        "Not found"
                    <p class="missing">
                        path
                    <a class="home" href="/">
                        "Home"

            fn LoginForm()
                var state = { name: "", password: "" }
                fn submit()
                    sys.login(state.name, state.password)
                fn render()
                    return <main class="login-form">
                        <h2>
                            "Sign in"
                        <div class="field">
                            <label class="name">
                                "Name"
                            <input type="text" class="name" value={state.name}>
                        <div class="field">
                            <label class="password">
                                "Password"
                            <input type="password" class="password" value={state.password}>
                        <button class="login-submit" onClick={submit}>
                            "Sign in"
                return render

            fn SignInBar()
                var state = { open: false }
                fn toggle()
                    state.open = !state.open
                fn render()
                    return <div class="sign-in-bar">
                        <button class="sign-in" onClick={toggle}>
                            if state.open
                                "Close"
                            else
                                "Sign in"
                        if state.open
                            <LoginForm>
                return render

            fn UserMenu()
                fn logout()
                    sys.logout()
                fn render()
                    var root = sys.schema(sys.resolve("/").typeName)
                    return <div class="user-menu">
                        <span class="user-name">
                            sys.field(currentUser, "name")
                        if canManageUsers
                            foreach p in root.props
                                if p.baseType == "set" && sys.schema(p.element).isPrincipal
                                    <a class="manage-users" href={sys.nest("/", p.name)}>
                                        "Users"
                        <button class="logout" onClick={logout}>
                            "Log out"
                return render

            fn InputType(baseType)
                if baseType == "int"
                    return "number"
                if baseType == "decimal"
                    return "number"
                if baseType == "date"
                    return "date"
                if baseType == "password"
                    return "password"
                return "text"

            fn boolGlyph(v)
                if v
                    return "✓"
                return "✗"
        """;

    // The effective ui for rendering: the app's ui augmented with the generic library (ALWAYS), plus —
    // when the app has no custom `fn render()` — a single synthesized generic `fn render()` that routes
    // every URL itself by composing the library (the DEFAULT UI). Functions are renumbered (CodeIds)
    // over the whole set so server and client key the memo cache alike.
    //
    // The library is PUBLIC: a custom `fn render()` reaches it through the scope chain (the renderer
    // parents the app scope under the library scope), so it can compose <ObjectForm> &c. — hence the
    // library functions + descriptors are synthesized for EVERY app, custom or not. The synthesized
    // generic render is added ONLY for a generic-UI app (no custom render); a custom app owns its own
    // routing.
    //
    // The generic render IS the collapse of the old per-URL C# dispatch (SsrRenderer.ResolveView +
    // the Synth*View builders): instead of synthesizing one anonymous view per type/prop and a C#
    // cardinality-walk to pick one, a single render calls `sys.resolve(path)` (the Code twin of that
    // walk) and switches on the resolved kind, binding the SAME library components the views used to.
    // So every page is now the custom-render path — the client always runs `render` with no ViewInfo.
    //
    // SystemNames lists the synthesized framework members (the library functions + the generic render)
    // so the renderer places them in the LIBRARY scope, between the system scope and the app scope.
    // IsGeneric is true when the synthesized generic render is in play (no custom render): the renderer
    // runs the render in the lib scope and keeps the generic breadcrumb/title chrome. Descriptors maps
    // "TypeName" → a type's descriptor literal and "Owner/prop" → a dict prop's descriptor, threaded
    // into the executor so `sys.schema(...)` resolves a shape.
    public static (InstanceUi? Ui, IReadOnlySet<string> SystemNames, bool IsGeneric,
        IReadOnlyDictionary<string, CodeObject> Descriptors) Effective(InstanceDescription desc)
    {
        // A custom `fn render()` owns the whole URL space, so it gets the library but no generic
        // render. A plain app — no `ui` section, or only common helpers — renders entirely through
        // the synthesized generic render over the Code ObjectForm library (the DEFAULT UI).
        var ui = desc.Ui ?? new InstanceUi();
        var isCustom = ui.Render != null;

        // Parse the library FRESH (distinct CodeFunction instances each call, so concurrent
        // renderers never share mutable Ids). StdlibSource defines the library functions plus a
        // `fn render()` (the generic router) — MapUi pulls the latter into libUi.Render.
        var (_, libUi) = CodeParse.ParseDesign(StdlibSource);
        var library = libUi.Functions ?? [];
        var genericRender = libUi.Render
            ?? throw new InvalidOperationException("The generic library must define `fn render()`.");

        var objectTypes = (desc.Types ?? []).Where(t => t.BaseType == BaseType.Object).ToList();

        // The render: the app's own when custom; otherwise the synthesized generic router (which
        // composes the library to route every URL via sys.resolve).
        var render = isCustom ? ui.Render : genericRender;

        var functions = new List<CodeFunction>();
        functions.AddRange(library);
        functions.AddRange(ui.Functions ?? []);

        var effective = ui with { Functions = functions, Render = render };
        // Number every function (library + app + the render) deterministically so the
        // server and the shipped client key the memo cache identically.
        CodeIds.Assign(new InstanceDescription(Types: desc.Types, Ui: effective, Common: desc.Common));

        // The framework-synthesized members — the renderer puts these in the library scope, between
        // the system scope and the app scope. The library functions are always synthesized; the
        // generic render joins them only when it is in play (a generic app), so it runs in the lib
        // scope and resolves the library components by name. Descriptors are resolved by `sys.schema`.
        var systemNames = new HashSet<string>(library.Where(f => f.Name != null).Select(f => f.Name!));
        return (effective, systemNames, IsGeneric: !isCustom, Descriptors(objectTypes, desc));
    }

    // ── the descriptor registry ──────────────────────────────────────────────────────

    // The descriptor literals threaded into the executor for `sys.schema(...)` to evaluate (the
    // replacement for the old `__descs`/`__dictDescs` globals). Two key shapes, both pure data:
    //   "TypeName"      → { name, labelProp, props } — a type's descriptor (sys.schema("T")).
    //   "Owner/prop"    → that prop's descriptor — sys.schema("O","P"). One entry per prop of every
    //                     object type, so a caller can fetch a single prop's shape by name. The
    //                     generic UI uses this for a dict route; a hand-written `fn render()` uses it
    //                     to compose <Input>/<Field> over a chosen prop (e.g. sys.schema("TodoItem","text")).
    // Built once per render from the schema; the executor evaluates + caches the one a call names and
    // ships it to the client like extent.
    private static Dictionary<string, CodeObject> Descriptors(List<TypeDefinition> objectTypes, InstanceDescription desc)
    {
        var map = objectTypes.ToDictionary(t => t.Name, t => TypeDescriptor(t, desc));
        foreach (var t in objectTypes)
            foreach (var p in t.Props ?? [])
                // A `password`-typed prop IS now a visible field (the M-auth `password` type): its descriptor
                // carries baseType "password" so Input renders a masked <input type="password"> bound to
                // sys.field(obj,"password") — which reads the BLANKED "" the load chokepoint ships (never the
                // hash). So the field is editable from the form (set/change a password) while the value never
                // leaks; it is only kept OUT of table columns / labelProp (Scalars excludes it).
                map[t.Name + "/" + p.Name] = PropDesc(p, desc);
        return map;
    }

    // The props of a type that the reflective UI surfaces — EVERY declared prop. A `password`-typed prop is
    // included (the M-auth `password` type): it renders a masked control bound to the blanked-"" value, so a
    // password can be SET/changed from the object form. (It is excluded only from columns/labelProp, by
    // Scalars — long-form/secret fields don't belong in a scannable list.)
    private static IEnumerable<PropDefinition> VisibleProps(TypeDefinition t) => t.Props ?? [];

    private static CodeObject TypeDescriptor(TypeDefinition t, InstanceDescription desc)
    {
        var scalars = Scalars(t, desc);
        // Prefer a text prop; else the first scalar EXCEPT an image (a content hash is not a label —
        // assets-design.md). Image stays a valid Scalars() member otherwise (a DictTable value column
        // still shows it — only labelProp candidacy excludes it, a narrower exclusion than password's).
        var labelProp = scalars.FirstOrDefault(p => p.Type == "text")?.Name
            ?? scalars.FirstOrDefault(p => p.Type != "image")?.Name ?? "";
        return Obj(
            ("name", Text(t.Name)),
            ("labelProp", Text(labelProp)),
            // True for the framework's principal type — the type carrying a `password`-typed credential
            // field (the M-auth `password` type). Sourced from the field's TYPE, so there is no magic
            // "User" string here. <UserMenu> reads it (via sys.schema(p.element).isPrincipal) to find the
            // root's user collection BY TYPE for the "Users" management link.
            ("isPrincipal", new CodeBool { Value = (t.Props ?? []).Any(IsPassword) }),
            ("props", Arr(VisibleProps(t).Select(p => (ICodeValue)PropDesc(p, desc)))));
    }

    // A prop descriptor: scalar { name, baseType }; reference { name, baseType:"object",
    // target } and set { name, baseType:"set", element } carry the OTHER type's name (the
    // component resolves it via sys.schema(name) — cycle-safe). A dictionary
    // { name, baseType:"dictionary", keyType, element, isScalar, valueProps } is self-contained
    // (dictTable reads it directly): valueProps are the element's scalar columns (empty for a
    // scalar dict, where isScalar=true and a single "Value" column is shown). sys.new(desc) reads
    // these to mint the New-entry draft — a `value` for a scalar dict (defaulted by `element`), else
    // one field per valueProp.
    private static CodeObject PropDesc(PropDefinition p, InstanceDescription desc)
    {
        if (p.Cardinality == Cardinality.Dictionary)
        {
            var isScalar = !desc.IsObjectType(p.Type);
            var valueProps = isScalar ? [] : Scalars(desc.FindType(p.Type)!, desc);
            return Obj(
                ("name", Text(p.Name)),
                ("baseType", Text("dictionary")),
                ("keyType", Text(p.KeyType ?? "text")),
                ("element", Text(p.Type)),
                ("isScalar", new CodeBool { Value = isScalar }),
                ("valueProps", Arr(valueProps.Select(vp => (ICodeValue)PropDesc(vp, desc)))),
                MultilineField(false));
        }
        if (p.Cardinality == Cardinality.Set)
            return Obj(("name", Text(p.Name)), ("baseType", Text("set")), ("element", Text(p.Type)), MultilineField(false));
        if (desc.IsObjectType(p.Type))
            return Obj(("name", Text(p.Name)), ("baseType", Text("object")), ("target", Text(p.Type)), MultilineField(false));
        // An enum scalar prop: { name, baseType: "enum", values: [...] } so objectForm renders a
        // <select> of its values (a bare `baseType: <typeName>` would fall through to a text input).
        if (desc.FindType(p.Type) is { BaseType: BaseType.Enum, Values: { } values })
            return Obj(("name", Text(p.Name)), ("baseType", Text("enum")),
                ("values", Arr(values.Select(v => (ICodeValue)Text(v)))), MultilineField(false));
        // A leaf scalar prop: { name, baseType }. A text prop carries `multiline` true so Input
        // renders a <textarea> instead of an <input>, and SetTable drops it from the columns (long-form
        // text belongs on the member page, not a scannable list). EVERY prop descriptor carries
        // `multiline` (false for all non-multiline-text) so the SetTable column filter can read it
        // uniformly — the interpreter evaluates both operands of `&&`, so it has no field to skip.
        return Obj(("name", Text(p.Name)), ("baseType", Text(p.Type)), MultilineField(p.Multiline));
    }

    // Every prop descriptor carries a `multiline` bool so consumers (Input, the SetTable column filter)
    // read it without a missing-field error; only a multiline text leaf is ever true.
    private static (string, ICodeValue) MultilineField(bool value) =>
        ("multiline", new CodeBool { Value = value });

    // A `password`-typed single scalar (the M-auth `password` type) — the descriptor side's mirror of
    // `IsPasswordProp` / the `password`-type read-blank, the ONE place here that recognizes the credential
    // field (isPrincipal keys on it; Scalars excludes it from columns/labelProp). Keyed on the declared
    // type, the same shape DbBridge/AccessFloor/WsHandler key their chokepoints on.
    private static bool IsPassword(PropDefinition p) =>
        p.Cardinality == Cardinality.Single && p.Type == "password";

    // Scalar (leaf-valued) props for the label prop and the table columns: base
    // leaves and enums (an enum value is text-shaped). References/sets/dicts are excluded — and so is a
    // `password`-typed field (the M-auth `password` type): it is a visible FORM field (set/change a
    // password) but never a labelProp or a table column (a secret is not a scannable list value).
    private static List<PropDefinition> Scalars(TypeDefinition t, InstanceDescription desc) => (t.Props ?? [])
        .Where(p => p.Cardinality == Cardinality.Single && !IsPassword(p)
            && (BaseTypes.IsName(p.Type) || desc.IsEnumType(p.Type)))
        .ToList();

    // ── tiny AST builders (the descriptor literals only) ──────────────────────────────
    // The generic UI's per-URL views are now a single synthesized `fn render()` written in
    // StdlibSource, so the only ASTs built in C# are the pure-data type/prop descriptors below.

    private static CodeText Text(string v) => new() { Value = v };
    private static CodeObject Obj(params (string Name, ICodeValue Value)[] props) =>
        new() { Props = props.Select(p => new CodeObjectProp { Name = p.Name, Value = p.Value }).ToArray() };
    private static CodeArray Arr(IEnumerable<ICodeValue> items) => new() { Items = items.ToArray() };
}
