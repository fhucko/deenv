using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeEnv.Code;
using DeEnv.Code.Parsing;
using DeEnv.Designer;
using DeEnv.Instance;
using DeEnv.Kernel;
using DeEnv.Storage;

namespace DeEnv.Http;

public sealed class SsrRenderer
{
    private readonly IInstanceStore _store;
    private readonly InstanceDescription _desc;
    private readonly TypeResolver _resolver;
    private readonly ClientSessionStore? _sessions;

    // The ui actually rendered/shipped: the app's `fn render()`, or — the default — the
    // self-hosted generic UI (objectForm/refEditor/setTable/dictTable/leafForm + synthesized
    // per-type views). Every page is code-owned now; there is no C# auto-form. The canonical
    // _desc stays pristine for printing; _ui carries the render-time synthesis.
    private readonly InstanceUi? _ui;

    // The asset authority (host:port where /ws and /js are served) — injected into the page so the
    // client loads its bundle and opens its WebSocket against it, keeping the app port a clean,
    // reserved-path-free data URL space. A full authority (not a bare port) so the asset port is a
    // kernel-level shared port, decoupled from the per-instance app addressing. Defaults to empty
    // (no asset host — a render with no client bundle, e.g. a unit-level render).

    // Names of the synthesized framework members (the generic library + the generic render) — placed
    // in the lib scope, between the system scope and the app scope, so user code can compose them but
    // they never pollute the app scope.
    private readonly IReadOnlySet<string> _systemNames;

    // True when the rendered `fn render()` is the framework-synthesized generic router (the app has
    // no custom render): it runs in the lib scope (to resolve the library components by name) and the
    // page keeps the generic breadcrumb/title chrome. A custom render runs in the app scope and owns
    // the whole page. The single signal that replaced the per-URL ViewKind dispatch.
    private readonly bool _isGeneric;

    // typeName → the type's descriptor literal, threaded into the CodeExecutor so `sys.schema(typeName)`
    // resolves a type's shape from the schema (the replacement for the `__descs` global). Now built for
    // every app (custom included), since `sys.schema(...)` is usable from a custom `fn render()` too.
    private readonly IReadOnlyDictionary<string, CodeObject> _descriptors;

    // The kernel's instance registry as a live DATA cell (app + ports per hosted instance), surfaced
    // to image Code as the read-only `instances` system global. Read PER RENDER (`.Current`), so every
    // fresh render reflects the kernel's CURRENT instances — a newly-created instance shows up on every
    // instance's next request, not a frozen boot snapshot. A var-shaped cell (not a pull-function), so a
    // future live-update path can hang change-notification on it. Defaults to empty (no kernel ⇒ no list).
    private readonly LiveRegistry _registry;

    // The instance's DISPLAY NAME (the kernel registry `app` label, e.g. "devlog"), used as the
    // generic-UI breadcrumb/title ROOT label (humanized → "Devlog") instead of the hardcoded root-type
    // name "Db". Threaded from the kernel through ContentHandler/InstanceApp, since the mount `base`
    // is NOT a reliable source (it is "/" behind nginx via X-Forwarded-Prefix). Empty (→ "Db" root) for
    // a bare/unit render that has no name to show. Also shipped to the client as window.initAppName so a
    // client-side (SPA) breadcrumb/title rebuild shows the same root label.
    private readonly string _appName;

    // The kernel-wired publish-preview delegate (M13 Track-B B3) threaded into the render executor so
    // `sys.publishPreview(design, targetId)` computes the dry-run PublishReport. Built by the KERNEL (it
    // alone reaches the target instance's data file + published-commit stamp cross-instance) and passed in
    // at both SsrRenderer construction sites (ContentHandler SSR + WsHandler refetch), so the preview
    // computes on first paint AND on the toggle→refetch. Null for a non-kernel host (the in-process test
    // server) — `sys.publishPreview` is then simply not reachable there, which is fine (only the designer
    // IDE, hosted by the kernel, calls it). Traffics only Code-layer types: the design ExecObject + the
    // target's int runtimeId + the render context, an IExecValue report out.
    private readonly Func<ExecObject, int, ExecContext, IExecValue>? _publishPreview;

    public SsrRenderer(IInstanceStore store, InstanceDescription desc, ClientSessionStore? sessions = null,
        LiveRegistry? registry = null, string appName = "",
        Func<ExecObject, int, ExecContext, IExecValue>? publishPreview = null)
    {
        _store = store;
        _desc = desc;
        _resolver = new TypeResolver(desc);
        _sessions = sessions;
        _registry = registry ?? new LiveRegistry();
        _appName = appName;
        _publishPreview = publishPreview;
        (_ui, _systemNames, _isGeneric, _descriptors) = GenericUi.Effective(desc);
    }

    // The rendered HTML plus the first-paint HTTP status (200 unless code set it, e.g. the
    // self-hosted NotFound view sets 404).
    //
    // `urlPath` is ROOT-RELATIVE — the instance is mount-UNAWARE, so its routing (`path` var, link
    // targets) is the same whether it lives at a path or a domain root. `base` is the mount prefix
    // the EDGES apply (the kernel router stripped it before this, and re-applies it on emitted links
    // so the app keeps writing `/notes/2`): "/" = root-mounted (behavior-preserving — every edge is
    // an identity), "/apps/todo" = path-mounted. `assetAuthority` (host:port) is where /ws + /js live.
    // `principalUserId` (M-auth) is the bound principal — the id of the `User` object the request acts
    // as, or null when anonymous. It resolves to the read-only `currentUser` system var and feeds the
    // access read floor (a denied object never enters the `db` graph). Floor-first: the test harness
    // passes it directly; a later slice binds it on the WS session (ClientSession.PrincipalUserId), the
    // durable home this threads from. With no access rules the app is dormant and the principal is inert.
    // `seed` (client data layer, slice 1a) reproduces the client's exact component view-state: a map
    // from a component's render-slot (`comp:<slotpath>`) to its setup-scope locals (`state`), applied
    // after that component's setup runs so the server renders the same tree the client has (e.g. a
    // popup the client toggled open) and harvests the data it demands. Null = today's behavior (the
    // setup defaults stand). The SHIP of state from the client + the refetch threading are later slices.
    public (string Html, int Status) Render(string urlPath, string @base = "/", string assetAuthority = "",
        int? principalUserId = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IExecValue>>? seed = null) =>
        RenderPage(urlPath, @base, assetAuthority, principalUserId, seed);

    // ── code-owned UI (every page is `fn render()`) ─────────────────────────────
    //
    // Every app — custom or generic — renders through a single `fn render()`. A custom render is
    // the app's own; a generic app's render is the framework-synthesized router (GenericUi), which
    // calls `sys.resolve(path)` and composes the library. The C# per-URL view dispatch is gone — its
    // routing now lives in Code (sys.resolve), proven by the SelfHostedUi generic-UI + resolve-probe
    // scenarios. A runtime error on first paint becomes an SSR error page.

    private (string Html, int Status) RenderPage(string urlPath, string @base, string assetAuthority,
        int? principalUserId = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IExecValue>>? seed = null)
    {
        var context = new ExecContext { Seed = seed };
        try
        {
            // Snapshot the store's version BEFORE the render reads it (optimistic-concurrency anti-clobber
            // — DECISIONS.md "App versioning — the full design (M13 clump)"): the two reads (version, then
            // the render's own store load) are not one atomic critical section, so reading version-first is
            // the SAFE order — a concurrent write landing in the gap can only make this an UNDER-estimate
            // (never claim freshness the render didn't actually see), which fails toward an honest-but-
            // spurious reject on a later commit, never a silent clobber.
            var storeVersion = _store.CurrentVersion;
            var blobBase = BlobBase(@base, assetAuthority);
            var (result, title, scope, status, trail) = ExecuteRender(urlPath, context, principalUserId: principalUserId, blobBase: blobBase);
            var body = new StringBuilder();
            SerializeChild(result, body, @base);

            // Mint a session and ship its clientId, so the WS can claim it (hello) and a
            // later milestone can hang per-client push on it. The id is all the client
            // needs; a refetch re-renders over a fresh store load. See ClientSession.
            var clientId = _sessions?.Create(principalUserId).Id ?? "";

            // First-paint state: only what the client-run render accessed (access-scoped,
            // sensitive fields denied) + the client-facing AST. Server-only functions and
            // the var initializers (which may compute from withheld data) never ship — the
            // client re-defines client functions and reads var *values* from initData.
            var initData = ClientState.Serialize(scope, context).ToJsonString();
            var clientUi = new InstanceUi(
                Vars: null,
                Functions: _ui!.Functions?.Where(f => !f.ServerOnly).ToList(),
                Render: _ui.Render);
            var clientCommon = _desc.Common?.Functions is { } common
                ? new InstanceCommon(common.Where(f => !f.ServerOnly).ToList())
                : null;

            var initUi = new JsonObject
            {
                ["ui"] = JsonSerializer.SerializeToNode(clientUi, SchemaJson.Options),
                ["common"] = clientCommon is null ? null : JsonSerializer.SerializeToNode(clientCommon, SchemaJson.Options),
            }.ToJsonString();

            // A generic-UI page keeps the breadcrumb chrome (plain links) around its content, so a
            // routed/collection/missing page can still navigate back up; a full-custom render owns the
            // whole page. The trail's HREFS are the cumulative URL prefixes (segment for segment, per
            // INSTANCE_MODEL.md), but its visible TEXT is the LABELED trail (humanized props + object
            // labels) — the same location-mirrors-URL invariant the client preserves.
            var breadcrumbs = _isGeneric ? Breadcrumbs(ParsePath(urlPath), trail, @base) : "";

            return (UiLayout(title, breadcrumbs, body.ToString(), ScriptSafe(initData), ScriptSafe(initUi),
                    clientId, @base, assetAuthority, _appName, _isGeneric, storeVersion, blobBase),
                status);
        }
        catch (CodeRuntimeException ex)
        {
            // A user-code error: its message belongs on the page.
            return (Layout("Error", $"<main><h1>Error</h1><p>{Escape(ex.Message)}</p></main>"), 200);
        }
        catch (Exception ex)
        {
            // An engine bug: log the details, show nothing internal.
            Console.Error.WriteLine($"SSR render of '{urlPath}' failed: {ex}");
            return (Layout("Error", "<main><h1>Error</h1><p>Internal error.</p></main>"), 200);
        }
    }

    // Neutralise "</script>" (and any "<") so an embedded JSON literal can't break out
    // of the inline <script> element.
    private static string ScriptSafe(string json) => json.Replace("<", "\\u003c");

    // Re-render to a client-state payload ({ leaves, scope, cache }) without producing
    // HTML. Used by the WS `refetch` (Stage 4b): after a mutation the client re-asks for
    // the entries it cannot recompute locally (hidden deps). The render runs over the
    // session's warm db graph (kept in sync with the client's mutations) with the client's
    // session vars, and returns authoritative state.
    // `lastIdFloor` is the client's current transient-id counter: the re-render mints
    // its transients below it, so shipped negative ids never collide with the drafts
    // the client already holds.
    // `seed` (client data layer, slice 1a/1b) reproduces the client's component view-state on the refetch
    // path too — same shape + effect as Render's. The WS now ships this state (slice 1b: ws.ts slotState →
    // HandleRefetch rebuilds it via SlotStateFromWire), so a client-toggled popup's demanded data is
    // harvested + shipped. Null = today's behavior (no mounted stateful component shipped state).
    // `harvestAction` (client data layer, slice 4 — the action-miss round-trip) is the (slot, fn-id) key of a
    // CLICK HANDLER the client could not complete because it read un-shipped data. When present, the server
    // reproduces the render (binding that handler's closure — its captured row/locals), then INVOKES it
    // READ-ONLY to harvest the data it reads (structural privacy ships it); the client merges it + re-invokes
    // the handler over the now-present data. Null = a normal render miss (today's path), no handler invoke.
    public JsonObject RenderState(
        string urlPath, IReadOnlyDictionary<string, IExecValue>? sessionVars, ExecObject? warmDb,
        int lastIdFloor = 0, int? principalUserId = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IExecValue>>? seed = null,
        string? harvestAction = null)
    {
        // Version-before-render, same safe-under-race ordering as RenderPage (see its comment): the
        // caller (HandleRefetch) already loaded `warmDb` fresh from the store just before this call, so
        // this snapshot reflects (at worst, slightly conservatively) the data the render below sees.
        var storeVersion = _store.CurrentVersion;
        var context = new ExecContext { Seed = seed };
        context.LastId.Value = Math.Min(0, lastIdFloor);
        var (_, _, scope, _, _) = ExecuteRender(urlPath, context, sessionVars, warmDb, principalUserId, harvestAction);
        var state = ClientState.Serialize(scope, context);
        state["storeVersion"] = storeVersion;
        return state;
    }

    // `Trail` is the labeled breadcrumb segments — one human-readable label per URL path segment
    // (a humanized prop name, or a member's labelProp value), in order. Empty for a custom render
    // (which owns its own chrome) or the root page. `Title` already joins them under the root label.
    private (IExecTagChild Result, string Title, ExecScope Scope, int Status, IReadOnlyList<string> Trail) ExecuteRender(
        string urlPath, ExecContext context, IReadOnlyDictionary<string, IExecValue>? sessionVars = null,
        ExecObject? warmDb = null, int? principalUserId = null, string? harvestAction = null, string blobBase = "")
    {
        var ui = _ui!;
        // Action-miss harvest (client data layer, slice 4): opt the render into indexing its onClick handler
        // closures (by (slot, fn-id)), so after the render the named handler can be looked up + invoked
        // read-only to harvest the data it reads. Only when an action is present, so a normal render is
        // unchanged (HandlerIndex stays null → ExecuteTag does no indexing).
        if (harvestAction != null) context.HandlerIndex = new Dictionary<string, ExecFunction>();

        // The bound principal + the access read floor (M-auth), built FIRST so the SAME floor gates BOTH
        // the graph load (DbBridge.LoadRoot below) AND the executor's `sys.extent(...)` listing (threaded
        // into the CodeExecutor): a read-denied row stays out of the graph AND out of any pick-candidate /
        // custom-render listing. `currentUser` (its scalar fields — e.g. `role`) is the principal the
        // request acts as, or ExecNull when anonymous / unresolved; the floor is dormant (allow-all) when
        // the app declares no rules. Property access on a null principal fails closed (a role rule denies).
        var currentUser = LoadPrincipal(principalUserId);
        var floor = new AccessFloor(_desc.Rules ?? [], currentUser);

        var exec = new CodeExecutor(_store, _descriptors, _resolver, floor, BuildCommitDiffReport, _publishPreview, BuildMergePreviewReport, BuildEvalContext, blobBase);
        // Ship EVERY schema descriptor on first paint (not lazily on first use), so a component
        // composing sys.schema(...) over a row that appears only after a client-side add still finds
        // its descriptor in the cache instead of missing. Descriptors are static, user-data-free.
        exec.PrewarmDescriptors(context);
        // Ship every type's write capabilities too (sys.canWrite create/edit/delete), so a client
        // navigation to a not-yet-rendered view (e.g. a freshly-created object's form) never misses → no
        // disruptive refetch. Mirrors PrewarmDescriptors; the floor is the only input.
        exec.PrewarmCapabilities(context);

        // Three scopes, chained system ← lib ← app, so the generic-UI library is a PUBLIC layer
        // between framework state and the user's code:
        //   system   — framework state (db, path, status), the shared root both can read;
        //   lib      — the synthesized generic library (ObjectForm/RefEditor/SetTable/…) plus the
        //              synthesized generic render, the PARENT of app, so a custom `fn render()` reaches
        //              the components through the scope chain (recognition is name-resolution) and
        //              composes them. The generic render runs IN lib (it composes the library), and
        //              since app is BELOW lib it still cannot see the user's app vars (type descriptors
        //              are no longer a var — they come via the sys.schema builtin);
        //   app      — the user's own vars/functions/render.
        var system = new ExecScope { IsTop = true };
        var lib = new ExecScope { Parent = system, IsTop = true };
        var app = new ExecScope { Parent = lib, IsTop = true };

        // `currentUser` (built above, with the floor) is exposed to Code as a READ-ONLY system var,
        // beside db/path/status. The access read floor evaluates each rule's condition over
        // { currentUser, object }; an anonymous request reads `null.role` → null (fail closed), so a role
        // rule denies.
        system.Items["currentUser"] = new ExecScopeItem { Value = currentUser, IsReadOnly = true };

        // `anonymousLockedOut` (M-auth login UI) — a read-only system var beside `currentUser`: true when
        // the app has rules and no `read` rule grants anonymous, so an un-logged-in visitor can read
        // nothing. Computed from the RULES alone (data-independent — see AccessFloor.AnonymousLockedOut),
        // so it is correct even when every list is empty. The synthesized generic render reads it to gate
        // an anonymous request to a <LoginForm>. Shipped in the scope exactly like currentUser.
        system.Items["anonymousLockedOut"] = new ExecScopeItem
        {
            Value = new ExecBool { Value = AccessFloor.AnonymousLockedOut(_desc.Rules ?? []) },
            IsReadOnly = true,
        };

        // `accessActive` (M-auth login UI) — true when the app has ANY access rule, i.e. auth is turned on.
        // Distinct from `anonymousLockedOut`: a PUBLIC app (a bare `read` rule grants anonymous) is NOT
        // locked out, yet still has auth, so it must offer the operator a way to log in. The synthesized
        // render reads it to show a <SignInBar> on a public auth app (login is a state, no reserved URL),
        // while a DORMANT no-auth app (accessActive false) shows no sign-in control. Rules-only (data-
        // independent), shipped like currentUser/anonymousLockedOut.
        system.Items["accessActive"] = new ExecScopeItem
        {
            Value = new ExecBool { Value = (_desc.Rules?.Count ?? 0) > 0 },
            IsReadOnly = true,
        };

        // `canManageUsers` (M-auth user admin) — true when auth is ON and the principal may EDIT User objects
        // (the write floor's User `edit` capability). The generic UI reads it to show admin-only user
        // management WITHOUT shipping the principal's role: the role stays private (the floor-hardening
        // invariant) and only this derived capability bit ships. The `accessActive &&` guard is load-bearing:
        // a DORMANT (no-rules) app's floor is allow-all, which would otherwise report true on a no-auth app.
        // CAVEAT: evaluated over a throwaway EMPTY User target, so it is EXACT only for PRINCIPAL-ONLY
        // conditions (`currentUser.role == "Admin"`). A TARGET-referencing User-edit rule (e.g. `object.role
        // != "Admin"`) yields a PERMISSIVE over-approximation here (the control may show though some rows are
        // denied) — harmless, because the floor RE-decides every write over the REAL target (WsHandler). No
        // app uses such a rule today; tighten to an all-targets check if/when per-field/target rules land.
        system.Items["canManageUsers"] = new ExecScopeItem
        {
            Value = new ExecBool
            {
                Value = (_desc.Rules?.Count ?? 0) > 0
                    && floor.CanWrite("edit", UserConvention.TypeName,
                        AccessFloor.ScalarObject(UserConvention.TypeName, 0, new ObjectValue(new Dictionary<string, NodeValue>()), _desc)),
            },
            IsReadOnly = true,
        };

        // db root (the object graph), read-only. A recompute reuses the warm graph the
        // session holds (already reflecting the client's mutations) instead of reloading. The read floor
        // gates what enters the graph: an object the principal may not read never ships (denied set
        // member omitted, denied single reference → null).
        var db = warmDb ?? DbBridge.LoadRoot(_store, _desc, context, floor);
        system.Items["db"] = new ExecScopeItem { Value = db, IsReadOnly = true };

        // `sys` is the framework namespace object, read-only: it holds the less-common framework
        // members (the builtins are dispatched by the sys-rooted-callee rule; `sys.instances` is the
        // one stateful member). `sys.instances` is the kernel's instance registry — the list of
        // instances this kernel hosts (app name + ports), provided so image Code can render the list
        // itself (the first kernel-as-data read path). Built PER RENDER, so every render reflects the
        // kernel's CURRENT instances; empty when there is no kernel.
        system.Items["sys"] = new ExecScopeItem
        {
            Value = new ExecObject
            {
                Id = --context.LastId.Value,
                Props = new Dictionary<string, IExecValue> { ["instances"] = BuildRegistry(context) },
            },
            IsReadOnly = true,
        };

        // `status` is a framework state var (the first-paint HTTP status); the view may assign
        // it (e.g. NotFound sets `status = 404`). Read back after render. Default 200.
        system.Items["status"] = new ExecScopeItem { Value = new ExecInt { Value = 200 }, IsReadOnly = false };

        // Functions first (close over their scope → mutual recursion) so var initializers may
        // call them. The synthesized generic library goes in the lib scope; the user's
        // own functions (common + ui) go in the app scope.
        foreach (var f in _desc.Common?.Functions ?? []) DefineFunction(f, app);
        foreach (var f in ui.Functions ?? []) DefineFunction(f, _systemNames.Contains(f.Name ?? "") ? lib : app);

        // UI/session state. Each initializer is a memoized computation (`var:<name>`),
        // evaluated in its own scope. Any synthesized library var goes in the lib scope; the
        // user's vars go in the app scope.
        foreach (var v in ui.Vars ?? [])
        {
            var target = _systemNames.Contains(v.Name) ? lib : app;
            var value = v.Value is { } init
                ? CodeExecutor.Memoize($"var:{v.Name}", context, () => exec.ExecuteValue(init, target, context))
                : new ExecNull();
            target.Items[v.Name] = new ExecScopeItem { Value = value, IsReadOnly = false };
        }

        // Client-held session vars (a refetch) override the user's just-computed values, so the
        // re-render sees the same UI state the client has. Computed vars (e.g. a filtered list)
        // are not shipped by the client and so recompute fresh here.
        if (sessionVars != null)
            foreach (var (name, value) in sessionVars)
                if (app.Items.TryGetValue(name, out var it) && !it.IsReadOnly) it.Value = value;

        // `path` is framework-provided (not declared by the app), always the requested URL —
        // the request is authoritative for routing. It lives in the system scope (writable;
        // the client mirrors it to location.pathname and updates it on navigation).
        system.Items["path"] = new ExecScopeItem { Value = new ExecText { Value = urlPath }, IsReadOnly = false };

        // `isGeneric`: true only when the framework's own generic router owns this render — the one
        // case where a collection-prop link's target (sys.resolve(path)) is guaranteed handled. A
        // custom render owns its own routing and generally has no handler for that nested path
        // (ObjectForm's collection-prop link would 404/blank), so GenericUi gates on it.
        system.Items["isGeneric"] = new ExecScopeItem { Value = new ExecBool { Value = _isGeneric }, IsReadOnly = true };

        // The render: the app's own (custom) or the framework-synthesized generic router — already
        // chosen by GenericUi.Effective and stored as _ui.Render. The generic render runs in the
        // LIBRARY scope (it composes the library, resolving ObjectForm/… by name); a custom render
        // runs in the APP scope (and reaches the library through its parent, the lib scope). Either
        // way the chain reaches system (db/path/status). It takes no arguments: routing is internal
        // (the generic render calls sys.resolve(path); a custom render reads path itself).
        var renderFn = ui.Render
            ?? throw new CodeRuntimeException("No render function for this instance.");
        var renderScope = _isGeneric ? lib : app;

        // The framework provides the live root data context as ambient `ctx` (writes persist); a form
        // opens a staging child via ctx.new(). Inert until a form does.
        context.Ambient = new AmbientFrame("ctx", new ExecCtx { Live = true }, null);
        var result = exec.InvokeFunction(renderFn, [], renderScope, context);

        if (result is not IExecTagChild child)
            throw new CodeRuntimeException("The render did not return a renderable value.");

        // Action-miss harvest (client data layer, slice 4): the render reproduced the client's exact tree and
        // indexed its onClick handlers; now invoke the one the client clicked READ-ONLY, so the data IT reads
        // (which the render itself never touched — hence un-shipped) records as displayed leaves and ships.
        // Runs AFTER the render (so the handler's closure — its captured foreach row, its ambient ctx — is the
        // SAME one the client holds) and BEFORE the harvest serialize below. A no-op when the action's handler
        // is not in this render (a stale intent). The render() must restore the live ambient first — done above.
        if (harvestAction != null) exec.InvokeHandlerForHarvest(harvestAction, context);

        // The first-paint HTTP status: the (possibly render-assigned) `status` system var. The
        // generic render's NotFound branch sets it to 404; read back here.
        var status = system.Items.TryGetValue("status", out var s) && s.Value is ExecInt si ? si.Value : 200;

        // The generic-UI breadcrumb/title trail: one LABEL per URL segment (humanized prop name, or a
        // member's labelProp value), resolved over THIS render's scope/context so each resolved object's
        // label leaf ships (the client re-resolves the same trail on a SPA nav). Empty for the root page
        // or a custom render (which owns its chrome). Computed even for a NotFound page (status 404): the
        // valid prefixes humanize and the bad segment falls back to raw — and the client's syncBreadcrumbs
        // does the SAME on a 404 SPA nav, so the server/client trails stay byte-identical. Runs on the
        // same `db` the render read, so it adds no new store load.
        //
        // NOTE — this is INTENTIONALLY computed on the refetch path too (RenderState discards the returned
        // string trail but NOT its side effect): SegmentLabel records each path-ancestor's labelProp as an
        // accessed leaf, so a refetch into a deep route ships those labels and the client's post-refetch
        // breadcrumb stays byte-identical BY CONSTRUCTION — not by relying on the linking page having shipped
        // them earlier. The resolves are pure (no store load), bounded by URL depth; do not "optimize" them
        // away on the refetch path — that would reintroduce a latent server/client divergence on a refetch.
        var trail = _isGeneric ? LabelTrail(urlPath, exec, renderScope, context) : [];

        // Title: the app's `title` var (in the app scope) when set; otherwise — a generic-UI page — the
        // labeled trail under the instance's display name (e.g. "Devlog / Milestones / Gate #3"; just the
        // root label at "/"); else "DeEnv". A NotFound page (404) is not special-cased: its trail is the
        // root + the (raw) unresolved segment, so the <title> matches the breadcrumb AND the client's
        // syncBreadcrumbs sets the identical title on a 404 SPA nav (byte-identical, no divergence).
        var title = app.Items.TryGetValue("title", out var t) && t.Value is ExecText titleText
            ? titleText.Value
            : _isGeneric ? TitleFromTrail(trail)
            : "DeEnv";

        // Ship the render scope (lib for the generic render; app for a custom render → its vars) plus
        // the parents (ClientState walks up). Type descriptors ride the memo cache as `schema:*`
        // entries, not the scope.
        return (child, title, renderScope, status, trail);
    }

    // The labeled breadcrumb trail for a generic-UI page: one label per URL segment, in order. Each
    // segment's label is its resolved object's labelProp value when the segment addresses a member (a
    // set member / object-dict entry), else the humanized raw segment (a prop name / scalar-dict key).
    // SegmentLabel reuses the resolve walk and returns null for the humanize case (or a missing object),
    // so a deleted/unshipped node falls back to the raw segment rather than blanking. This is the C# half
    // of the location-mirrors-URL invariant; ui.ts's syncBreadcrumbs is the byte-identical client twin.
    private List<string> LabelTrail(string urlPath, CodeExecutor exec, ExecScope scope, ExecContext context)
    {
        var segments = urlPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var labels = new List<string>(segments.Length);
        var prefix = "";
        foreach (var seg in segments)
        {
            prefix += "/" + seg;
            labels.Add(exec.SegmentLabel(prefix, scope, context) ?? TextUtil.Humanize(seg));
        }
        return labels;
    }

    // Page shell for a code page: optional generic chrome (a generic-UI page keeps the
    // breadcrumbs) around the `#app` mount the client reconciles into; an inline bootstrap
    // injects the bundle from the infra port (/js), which hydrates from window.initUi /
    // window.initData. The default stylesheet ships on EVERY page: the generic UI's component
    // styles (.object-form/.set-table/.ref-editor/…) apply to its markup, and the base element
    // styles (typography, inputs, buttons, tables) give a custom `fn render()` app a clean look
    // too — a custom app overrides via the cascade. (Zero-config good defaults; minimal by default.)
    private static string UiLayout(
        string title, string breadcrumbs, string body, string initData, string initUi, string clientId,
        string @base, string assetAuthority, string appName, bool isGeneric, int storeVersion, string blobBase) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>{{Escape(title)}}</title>
          <style>{{ViewChromeCss}}</style>
          <script>window.initData={{initData}};window.initUi={{initUi}};window.initClientId="{{clientId}}";window.initBase="{{JsStringSafe(@base)}}";window.initAssetAuthority="{{JsStringSafe(assetAuthority)}}";window.initAppName="{{JsStringSafe(appName)}}";window.initIsGeneric={{(isGeneric ? "true" : "false")}};window.initStoreVersion={{storeVersion}};window.initBlobBase="{{JsStringSafe(blobBase)}}";</script>
          <script>(function(){var a=window.initAssetAuthority,b=window.initBase==="/"?"":window.initBase;var s=document.createElement("script");s.src=a?location.protocol+"//"+a+b+"/js":b+"/js";document.head.appendChild(s);})();</script>
        </head>
        <body>{{breadcrumbs}}<div id="app">{{body}}</div></body>
        </html>
        """;

    // The blob pool's SERVE base for THIS request (assets-design.md) — where `sys.assetUrl(name)`
    // resolves an <img src>. Ships to the client as window.initBlobBase (read back, byte-identical,
    // by sys.assetUrl on both twins — codeExec.ts's execAssetUrl), computed ONCE per page load exactly
    // like initAssetAuthority. Two shapes (slice 4, assets-design.md §Origin):
    //   • dev (no env var): derived like /ws and /js — asset authority + mount-base + "/assets".
    //   • prod (DEENV_PUBLIC_BLOB_BASE, e.g. "https://assets.deenv.org"): the dedicated blob domain,
    //     which carries the instance in its PATH — "<base>/<appName>" — because there is no app
    //     subdomain on that origin to carry it. Serve-only: the UPLOAD URL is NOT this base — ws.ts's
    //     uploadBlob posts to assetUrl("/assets") (same-origin on the app subdomain in prod, where the
    //     session cookie rides; nginx routes the POST by method).
    public static readonly string? PublicBlobBase =
        Environment.GetEnvironmentVariable("DEENV_PUBLIC_BLOB_BASE") is { Length: > 0 } v
            ? v.TrimEnd('/')
            : null;

    private string BlobBase(string @base, string assetAuthority) =>
        BlobBase(@base, assetAuthority, _appName, PublicBlobBase);

    public static string BlobBase(string @base, string assetAuthority, string appName, string? publicBlobBase) =>
        publicBlobBase is not null
            ? $"{publicBlobBase}/{appName}"
            : (assetAuthority.Length > 0 ? $"//{assetAuthority}" : "") + (@base == "/" ? "" : @base) + "/assets";

    // Escape a string for embedding inside a double-quoted JS string literal in the injected
    // bootstrap (the base + asset authority). Backslash/quote and "<" (so "</script>" can't break
    // out), matching ScriptSafe's intent. Both values are server-controlled (a path + host:port), so
    // this is defensive, not a security boundary.
    private static string JsStringSafe(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("<", "\\u003c");

    // The default stylesheet (served on every page — see UiLayout). Three layers: base element
    // styling (typography, form controls, buttons, tables) that lifts ANY page off raw HTML;
    // generic-UI component styling (.object-form/.set-table/.ref-editor/…); and the operator
    // designer's row classes (.type-row/.prop-row/.instance-row/…). Semantic button intent comes
    // from the components' own class names (add/create/save = primary green; remove/delete/clear
    // = danger). A custom app overrides any of this via a more-specific rule (the cascade).
    private const string ViewChromeCss = """
        :root {
          color-scheme: light;
          --bg: #f6f8fa; --surface: #fff; --border: #d0d7de; --border-soft: #eaeef2;
          --text: #1f2328; --muted: #57606a; --accent: #0969da; --green: #1f883d; --danger: #cf222e;
          --warn: #9a6700;
        }
        *, *::before, *::after { box-sizing: border-box; }
        body { font-family: system-ui, -apple-system, "Segoe UI", Roboto, Arial, sans-serif;
          margin: 0; padding: 1.5rem 1.5rem 4rem; color: var(--text); background: var(--bg); line-height: 1.5; }
        #app, body > nav.breadcrumbs { max-width: 900px; margin-left: auto; margin-right: auto; }
        h1, h2, h3 { line-height: 1.25; margin: 1.4rem 0 0.6rem; }
        h1 { font-size: 1.6rem; } h2 { font-size: 1.3rem; } h3 { font-size: 1.05rem; color: var(--muted); }
        h2:first-child, h3:first-child { margin-top: 0; }
        a { color: var(--accent); text-decoration: none; } a:hover { text-decoration: underline; }
        p { margin: 0.5rem 0; }
        nav.breadcrumbs { margin: 0 auto 1.2rem; color: var(--muted); font-size: 0.9rem; }
        nav.breadcrumbs a { color: var(--muted); }

        label { display: block; font-size: 0.8rem; font-weight: 600; color: var(--muted); margin-bottom: 0.2rem; }
        input, select, textarea { font: inherit; color: inherit; padding: 0.4rem 0.55rem;
          border: 1px solid var(--border); border-radius: 6px; background: var(--surface); }
        input:focus, select:focus, textarea:focus { outline: 2px solid color-mix(in srgb, var(--accent) 30%, transparent);
          outline-offset: 0; border-color: var(--accent); }
        /* Checkbox styled to match the input family (same border + radius + accent), not the bare
           native control — a custom box with a white check on :checked. Library-owned default. */
        input[type=checkbox] { appearance: none; -webkit-appearance: none; width: 1.15rem; height: 1.15rem;
          margin: 0; padding: 0; border: 1px solid var(--border); border-radius: 5px; background: var(--surface);
          vertical-align: middle; cursor: pointer; position: relative; transition: background .12s, border-color .12s; }
        input[type=checkbox]:hover { border-color: var(--accent); }
        input[type=checkbox]:checked { background: var(--accent); border-color: var(--accent); }
        input[type=checkbox]:checked::after { content: ""; position: absolute; left: 0.38rem; top: 0.17rem;
          width: 0.27rem; height: 0.55rem; border: solid #fff; border-width: 0 2px 2px 0; transform: rotate(45deg); }
        input[type=checkbox]:focus-visible { outline: 2px solid color-mix(in srgb, var(--accent) 30%, transparent); outline-offset: 1px; }
        textarea { width: 100%; min-height: 7rem; font-family: ui-monospace, "SF Mono", Menlo, Consolas, monospace; font-size: 0.85rem; }
        /* The "standard" Input variant (MUI-style): the library owns this look; callers opt in with
           <Input variant="standard">, never via their own CSS. Reads as plain text, reveals an
           underline affordance on hover, accent underline on focus. */
        input[variant="standard"] { border: 1px solid transparent; border-radius: 0; background: transparent; }
        input[variant="standard"]:hover { border-bottom-color: var(--border); }
        input[variant="standard"]:focus { outline: none; background: transparent; border-bottom-color: var(--accent); }

        button { font: inherit; padding: 0.4rem 0.85rem; border: 1px solid var(--border); border-radius: 6px;
          background: var(--surface); color: var(--text); cursor: pointer; transition: background .12s, border-color .12s; }
        button:hover { background: #f3f4f6; }
        button:active { background: #e9ebee; }
        .create-save, .dict-add, .add-type, .add-prop, .create-instance, .rename-save, .apply-design,
        .add-user, .add-list-btn, .add-item-btn {
          background: var(--green); border-color: var(--green); color: #fff; }
        .create-save:hover, .dict-add:hover, .add-type:hover, .add-prop:hover,
        .create-instance:hover, .rename-save:hover, .apply-design:hover,
        .add-user:hover, .add-list-btn:hover, .add-item-btn:hover { background: #1a7f37; border-color: #1a7f37; }
        .set-remove, .dict-remove, .ref-clear, .remove-type, .remove-prop, .delete-design, .delete-instance {
          color: var(--danger); }
        .set-remove:hover, .dict-remove:hover, .ref-clear:hover, .remove-type:hover, .remove-prop:hover,
        .delete-design:hover, .delete-instance:hover { background: #fff0f0; border-color: var(--danger); }

        .object-form, .ref-editor, .leaf-form, .create-form { background: var(--surface); border: 1px solid var(--border);
          border-radius: 10px; padding: 1.25rem 1.4rem; box-shadow: 0 1px 2px rgba(31,35,40,.05); }
        .field { margin-bottom: 0.9rem; }
        .field:last-child { margin-bottom: 0; }
        .field > input:not([type=checkbox]), .field > select { width: 100%; max-width: 440px; }
        a.list-title { display: inline-block; font-weight: 600; font-size: 0.95rem; }

        /* Data-conflict bar (M13 slice 6 coarse → Track-B B5 FINE): a loud tinted bar at the top of the
           object form when a same-field collision was rejected. B5 makes it FINE — the <ConflictBar> library
           component groups the collisions BY OBJECT (a labeled group per object, so two objects' `title`
           conflicts are distinguishable, not a flat "Title, Title"), shows each field's YOURS vs THEIRS value
           INLINE so the operator SEES both sides before choosing, and offers a per-field Keep mine / Take
           theirs pair (plus whole-bar "Keep all mine" / "Take all theirs" fallbacks). Button emphasis
           (three-lens review fix 4a — ux call, DECIDED, user can override): Take theirs is the SAFE action
           (discard-and-refresh, never touches another session's data) — it gets the SOLID/primary green
           treatment. Keep mine is the LOUD one (a deliberate force-overwrite of a colleague's change) — it
           gets the outlined/danger treatment, so the visually heaviest button is never the one that overwrites
           someone else's work. */
        .conflict-bar { border: 1px solid var(--danger); background: #fff0f0; color: var(--danger);
          border-radius: 8px; padding: 0.7rem 0.9rem; margin: 0 0 1rem; }
        .conflict-message { display: block; font-weight: 600; margin-bottom: 0.5rem; }
        .conflict-group { border-top: 1px solid rgba(207,34,46,.25); padding-top: 0.55rem; margin-top: 0.55rem; }
        .conflict-group:first-of-type { border-top: 0; padding-top: 0; margin-top: 0; }
        .conflict-group-label { font-weight: 700; font-size: 0.85rem; text-transform: uppercase;
          letter-spacing: .02em; margin-bottom: 0.35rem; }
        .conflict-field-row { display: flex; flex-wrap: wrap; align-items: center; gap: 0.6rem;
          padding: 0.35rem 0; }
        .conflict-field-name { font-weight: 600; min-width: 7rem; }
        .conflict-sides { display: flex; gap: 0.9rem; flex: 1 1 auto; color: var(--text); }
        .conflict-side-label { display: block; font-size: 0.7rem; text-transform: uppercase; letter-spacing: .02em;
          color: var(--muted); }
        .conflict-val { font-weight: 600; }
        .conflict-empty { font-style: italic; color: var(--muted); }
        .conflict-field-actions { display: flex; gap: 0.4rem; }
        .conflict-field-actions button, .conflict-actions button { font-size: 0.85rem; }
        .conflict-field-take, .conflict-take { border-color: var(--green); color: #fff; background: var(--green); }
        .conflict-field-keep, .conflict-keep { border-color: var(--danger); color: var(--danger); background: var(--surface); }
        .conflict-actions { display: flex; gap: 0.5rem; margin-top: 0.7rem; padding-top: 0.55rem;
          border-top: 1px solid rgba(207,34,46,.25); }

        .set-table table, .dict-table table { border-collapse: collapse; width: 100%; margin: 0.3rem 0 0.7rem;
          background: var(--surface); border-radius: 8px; overflow: hidden; border: 1px solid var(--border); }
        .set-table th, .dict-table th, .set-table td, .dict-table td { padding: 0.45rem 0.65rem; text-align: left;
          border-bottom: 1px solid var(--border-soft); }
        .set-head th, .dict-head th { background: var(--bg); font-size: 0.78rem; font-weight: 600; color: var(--muted);
          text-transform: uppercase; letter-spacing: .02em; }
        .set-row:hover td, .dict-row:hover td { background: #f9fafb; }
        .set-row input, .dict-row input, .set-row select, .dict-row select { max-width: 200px; }

        /* Whole-row navigation: each data row is relatively positioned so a stretched real anchor
           (a.row-link) covers it via ::after — the entire row is one click target (keyboard + mouse +
           new-tab, since it is a real <a>), while the identity text in the first cell still reads as
           plain text. A per-row Remove is z-raised above the overlay and revealed on hover/focus, so
           clicking it (which stops propagation) removes WITHOUT navigating.
           The overlay is scoped to NON-managed tables: an action-managed SetTable (one given rowActions)
           carries its own action buttons in each row, so a click-stealing whole-row overlay would sit
           over them. The .set-table.managed class (set when rowActions is provided) opts the row out of
           the stretch — the label stays an in-cell nav link, the action buttons stay directly clickable.
           This replaces the per-consumer z-index band-aid the designs list used to need. */
        .set-row, .dict-row { position: relative; cursor: pointer; }
        .set-table.managed .set-row { cursor: default; }
        .set-row td.row-id a.row-link, .dict-row td.row-id a.row-link { color: inherit; font-weight: 600; }
        .set-row td.row-id a.row-link:hover, .dict-row td.row-id a.row-link:hover { text-decoration: none; }
        .set-table:not(.managed) a.row-link::after, .dict-table a.row-link::after {
          content: ""; position: absolute; inset: 0; }
        .set-row td.row-action, .dict-row td.row-action { text-align: right; width: 1%; white-space: nowrap; }
        .set-remove, .dict-remove { position: relative; z-index: 1; opacity: 0; transition: opacity .12s;
          padding: 0.15rem 0.55rem; border-color: transparent; background: transparent; color: var(--muted); }
        .set-row:hover .set-remove, .dict-row:hover .dict-remove,
        .set-remove:focus-visible, .dict-remove:focus-visible { opacity: 1; }
        .set-remove:hover, .dict-remove:hover { color: var(--danger); background: #fff0f0; border-color: var(--danger); }
        .bool-cell { font-size: 1rem; line-height: 1; color: var(--muted); }

        /* Image scalar (assets-design.md): the object-form field (thumbnail + ImageInput + Clear) and
           the small table-cell thumbnail SetTable/DictTable show instead of a raw hash string. */
        .image-field { display: flex; flex-direction: column; align-items: flex-start; gap: 0.4rem; }
        .image-thumb { max-width: 160px; max-height: 160px; border-radius: 4px; border: 1px solid var(--border); }
        .image-clear { border-color: transparent; color: var(--muted); padding: 0.15rem 0.55rem; }
        .image-clear:hover { color: var(--danger); background: #fff0f0; border-color: var(--danger); }
        .thumb-cell { max-width: 48px; max-height: 48px; border-radius: 3px; vertical-align: middle; }

        .dict-error { color: var(--danger); font-size: 0.85rem; margin-top: 0.4rem; }
        .set-empty, .dict-empty { color: var(--muted); font-size: 0.9rem; margin: 0.1rem 0 0; }

        /* Flag-gated create view: the `+ New` button replaces the old always-visible inline add row;
           clicking it reveals a labeled create form (.create-form, reusing the .object-form card +
           .field labels) BELOW the still-visible read-only table OR ref editor, with Save (primary
           green, .create-save) and a plain Cancel — the list stays in view while appending. Both the
           SetTable and the RefEditor now gate their create behind this `+ New` toggle (the single
           create path is a nested create-mode ObjectForm). Hidden until asked — the create-then-
           populate model; collections are added on the entry's own page after it exists. */
        .new-btn { margin-top: 0.3rem; border-color: var(--accent); color: var(--accent); }
        .new-btn:hover { background: color-mix(in srgb, var(--accent) 8%, var(--surface)); border-color: var(--accent); }
        .create-form > .field > input:not([type=checkbox]), .create-form > .field > select { width: 100%; max-width: 440px; }
        /* A createForm slot body that groups its fields in a wrapper (the IDE's instance create form:
           a design <select> + the name <Field>) — match the standard full-width field layout so the
           controls don't stack at default widths. */
        .instance-create-fields .field > input:not([type=checkbox]), .instance-create-fields .field > select { width: 100%; max-width: 440px; }
        /* The generic RefSelect picker (lib): a bare full-width <select>, matching the name field's width. */
        .ref-select { width: 100%; max-width: 440px; }
        .create-actions { display: flex; gap: 0.5rem; align-items: center; margin-top: 1.1rem; }
        .ref-current { margin-bottom: 0.7rem; color: var(--muted); }
        .ref-type { margin-top: 0; }
        .ref-controls { display: flex; flex-wrap: wrap; gap: 0.5rem; align-items: center; margin-bottom: 0.7rem; }
        .ref-controls select.ref-pick { min-width: 200px; }

        /* Operator designer (custom fn render) */
        .ide { display: block; }
        nav.ide-nav { display: flex; gap: 1rem; padding-bottom: 0.8rem; margin-bottom: 1.2rem;
          border-bottom: 1px solid var(--border); }
        nav.ide-nav a { font-weight: 600; color: var(--muted); }
        /* The current section is marked with .is-active (the render derives it from the path) so the
           operator can see where they are. */
        nav.ide-nav a.is-active { color: var(--accent); }
        .designs-table, .instances-table { border-collapse: collapse; width: 100%; margin: 0.3rem 0 1rem;
          background: var(--surface); border: 1px solid var(--border); border-radius: 8px; overflow: hidden; }
        .designs-table th, .instances-table th, .designs-table td, .instances-table td {
          padding: 0.5rem 0.65rem; text-align: left; border-bottom: 1px solid var(--border-soft); }
        .designs-head th, .instances-head th { background: var(--bg); font-size: 0.78rem; font-weight: 600;
          color: var(--muted); text-transform: uppercase; letter-spacing: .02em; }
        .design-row:hover td, .instance-row:hover td { background: #f9fafb; }
        /* Each type is a CARD: a head (name + kind + remove) and, below, either its props editor
           (object kind) or its enum-values field (enum kind) — never both. */
        .design-editor > .add-type { margin: 0.3rem 0 0.2rem; }
        .type-card { background: var(--surface); border: 1px solid var(--border); border-radius: 10px;
          padding: 0.85rem 1rem; margin: 0.7rem 0; box-shadow: 0 1px 2px rgba(31,35,40,.05); }
        .type-head { display: flex; flex-wrap: wrap; gap: 0.5rem; align-items: center; }
        .type-head input.type-name { font-weight: 600; flex: 1 1 200px; min-width: 0; }
        .type-head select.type-kind { flex: 0 0 auto; }
        .type-head button.remove-type { margin-left: auto; }
        .enum-values { margin-top: 0.75rem; }
        .props-editor { margin-top: 0.75rem; }
        /* Props are a labeled grid: a header row of column names, then one row per prop. Flex (not a
           fixed grid) so the conditional key-type field slots in without breaking a column template. */
        /* Header and rows share ONE grid template, so columns line up by construction. The key-type
           column (4) is meaningful only for a dictionary, so it COLLAPSES — and its "Key type" header is
           omitted — unless this type actually has a dictionary prop. :has() keys off the row's is-dict
           class, so the column + header appear (and collapse) live, and on first paint, as cardinalities
           change. Name/Type/Cardinality keep columns 1–3; the remove button stays in column 5. */
        .prop-head, .prop-row { display: grid; gap: 0.5rem; align-items: center;
          grid-template-columns: minmax(0, 1.2fr) minmax(0, 1.3fr) minmax(0, 1fr) 0 auto; }
        .props-editor:has(.prop-row.is-dict) .prop-head,
        .props-editor:has(.prop-row.is-dict) .prop-row {
          grid-template-columns: minmax(0, 1.2fr) minmax(0, 1.3fr) minmax(0, 1fr) minmax(0, 1fr) auto; }
        .prop-head { font-size: 0.72rem; font-weight: 600; color: var(--muted); text-transform: uppercase;
          letter-spacing: .03em; padding: 0 0.1rem 0.25rem; }
        .props-editor:has(.prop-row.is-dict) .prop-head::after { content: "Key type"; grid-column: 4; }
        .prop-row input, .prop-row select { min-width: 0; width: 100%; }
        .prop-row input.prop-keytype { grid-column: 4; }
        .prop-row button.remove-prop { grid-column: 5; padding: 0.2rem 0.5rem; }
        /* The multiline toggle is a full-width sub-row under a text prop's inputs (a labeled checkbox).
           Spanning all columns keeps it off the name/type/cardinality grid; the checkbox sizes to its
           content (overriding the .prop-row input width:100% / min-width:0 above). */
        .prop-row label.multiline-toggle { grid-column: 1 / -1; display: flex; align-items: center;
          gap: 0.4rem; font-size: 0.8rem; color: var(--muted); margin-top: 0.15rem; }
        .prop-row label.multiline-toggle input.prop-multiline { width: auto; min-width: 0; margin: 0; }
        .props-editor > button.add-prop { margin-top: 0.55rem; }
        /* Progressive disclosure — driven by a class on the container, not by conditional DOM (a field
           appearing/disappearing inside a foreach row does not reliably reconcile; an attribute/class
           change on the stable container does). The fields stay in the DOM; only their visibility flips.
           keyType matters only for a dictionary; a type shows EITHER its props editor (object) OR its
           enum-values field (enum), never both. SchemaBridge already ignores the hidden field's value. */
        .prop-row:not(.is-dict) input.prop-keytype { display: none; }
        /* `multiline` is a presentation flag valid ONLY on a single text prop (the loader rejects it
           elsewhere); so the toggle shows only on an is-text-single row and collapses otherwise. */
        .prop-row:not(.is-text-single) label.multiline-toggle { display: none; }
        .type-card:not(.is-enum) .enum-values { display: none; }
        .type-card.is-enum .props-editor { display: none; }
        /* The structured-render tree editor (M12 E1): each element node is a labeled box; its children
           nest one indent level deeper, so the tree's shape reads visually. Leaf nodes (empty tag) show
           only their expr input. Scalar fields are inline inputs, like the type/prop editor above. */
        .render-tree { margin-top: 0.6rem; }
        .node-element { border-left: 2px solid var(--border); padding: 0.15rem 0 0.15rem 0.6rem; margin: 0.2rem 0; }
        .node-tag-row { display: flex; align-items: center; gap: 0.3rem; }
        .node-angle, .node-attr-eq { color: var(--muted); font-family: ui-monospace, monospace; }
        .node-tag { font-weight: 600; flex: 0 1 200px; min-width: 0; }
        .node-attr { display: flex; align-items: center; gap: 0.3rem; margin: 0.2rem 0 0.2rem 1rem; }
        .node-attr input { min-width: 0; flex: 0 1 180px; }
        .node-children { margin-left: 0.9rem; }
        /* A leaf has no tag, so it gets its own muted anchor (mirroring .node-angle's "<" on an element)
           so the lone input still reads as "this node's text/expression content", not an orphan field. */
        .node-leaf { display: flex; align-items: center; gap: 0.3rem; padding: 0.15rem 0; margin: 0.2rem 0; }
        .node-leaf-anchor { color: var(--muted); font-family: ui-monospace, monospace; }
        .node-leaf input.node-expr { width: 100%; max-width: 440px; font-family: ui-monospace, monospace; }
        /* E2 — structural editing. The remove (×) for a child node is passed down as an onRemove handler and
           rendered INSIDE that node's OWN tag-row (element) / leaf row (leaf) — right next to the input it
           removes, not as a sibling of the whole (possibly deep) subtree, so it never drifts far from the
           node it deletes or floats to the subtree's vertical middle. The root's invocation passes no
           onRemove (null), so it renders no × (the single-root invariant). The add-row's small buttons add a
           child element / text-or-expr leaf / attribute to the node itself. All × removes reuse the danger
           treatment (.remove-*), sized to sit tidily inline with the row's inputs. */
        .node-add-row { display: flex; gap: 0.3rem; margin: 0.3rem 0 0.2rem 0; }
        .node-add-row button.add-element, .node-add-row button.add-text, .node-add-row button.add-attr,
        .node-add-row button.add-for, .node-add-row button.add-if {
          padding: 0.15rem 0.5rem; font-size: 0.82rem; color: var(--muted); }
        button.remove-node, button.remove-attr, button.remove-use { padding: 0.1rem 0.45rem; color: var(--danger); flex: 0 0 auto; }
        button.remove-node:hover, button.remove-attr:hover, button.remove-use:hover { background: #fff0f0; border-color: var(--danger); }
        /* S5a — reorder. Same small inline-button treatment as the × controls, anchored beside them in each
           row's own header (the E2 anchoring precedent), muted rather than danger-colored (a reorder is not
           destructive). ux review (adjudicated over ui-arch): DISABLE-IN-PLACE at the first/last position,
           never hidden. The onRemove==null precedent (no × on a root row) is STATIC — a root row never
           reflows. First/last-of-siblings is DYNAMIC — it flips mid-interaction as rows move past it — so
           hiding ▼ at the last position would slide the destructive × into the slot the operator is
           chase-clicking, risking an unconfirmed subtree delete on overshoot. Disabled-in-place keeps ×
           anchored in a fixed slot; an edge click is a native no-op (a disabled button fires no click at
           all — moveRow's own neighbor==null no-op is defense in depth, not the primary guard). */
        button.move-up, button.move-down { padding: 0.1rem 0.4rem; color: var(--muted); flex: 0 0 auto; font-size: 0.8rem; }
        button.move-up:hover, button.move-down:hover { background: color-mix(in srgb, var(--accent) 8%, transparent); border-color: var(--accent); }
        button.move-up:disabled, button.move-down:disabled { color: color-mix(in srgb, var(--muted) 45%, transparent); cursor: default; }
        button.move-up:disabled:hover, button.move-down:disabled:hover { background: none; border-color: var(--border); }
        /* S6a — for/if control-flow ROWS in the tree editor: a distinct dashed-border box (vs the
           element's solid left border) so a loop/condition row reads as structurally different from a
           plain tag, echoing the canvas's own for-template/if-template marking below. */
        .node-for, .node-if { border-left: 2px dashed var(--border); padding: 0.15rem 0 0.15rem 0.6rem; margin: 0.2rem 0; }
        /* M12 S4b review fold (ux prescription, answering ui-arch's density concern): a row is now a
           click-to-select TARGET too, but the rows are dense with inputs/buttons, so a pointer cursor
           would lie (most of a row's area is an ordinary form control, not "click here to select"). A
           faint accent tint on hover — half the strength of the .is-selected tint below — signals
           "this is one actionable unit" without implying a link/button everywhere the cursor lands.
           :not(.is-selected) makes the selected state dominate unconditionally, regardless of
           stylesheet order (an already-selected row must never look merely hovered). */
        .node-element:hover:not(.is-selected), .node-for:hover:not(.is-selected),
        .node-if:hover:not(.is-selected), .node-leaf:hover:not(.is-selected) {
          background: color-mix(in srgb, var(--accent) 5%, transparent); border-radius: 4px; }
        .node-for-head, .node-if-head { display: flex; align-items: center; gap: 0.3rem; }
        .node-keyword { color: var(--muted); font-family: ui-monospace, monospace; font-weight: 600; }
        .node-for-item, .node-for-collection, .node-if-condition { min-width: 0; }
        .node-for-collection, .node-if-condition { flex: 1 1 auto; font-family: ui-monospace, monospace; }
        .node-branch { margin-left: 0.9rem; padding-left: 0.5rem; border-left: 1px dotted var(--border); }
        .branch-label { display: block; color: var(--muted); font-size: 0.78rem; text-transform: uppercase;
          letter-spacing: 0.03em; margin: 0.2rem 0; }
        button.add-else { padding: 0.15rem 0.5rem; font-size: 0.82rem; color: var(--muted); margin: 0.2rem 0; }
        /* The client-computable CANVAS (M12): a live structural view of the render tree, built by both twins
           from the MetaNode rows (sys.renderTree) — it repaints instantly as the tree editor is edited, no
           server round-trip. A bordered surface card; NOT pointer-events:none — S4 turns clicks
           here into node selection, so the surface must stay clickable. Expressions that can't evaluate
           client-side yet show as .expr-chip pills (a monospace placeholder meaning "an expression lives
           here"); the .is-empty variant marks a node with neither a tag nor an expression. */
        .design-canvas { border: 1px solid var(--border); border-radius: 10px; padding: 1rem; background: var(--surface); }
        .design-canvas .expr-chip { display: inline-block; padding: 0 0.35rem; border-radius: 4px; background: #eef1f5;
          color: var(--muted); font-family: ui-monospace, monospace; font-size: 0.85rem; }
        .design-canvas .expr-chip.is-empty { font-style: italic; background: #f6ecec; }
        /* M12 eval-degrade-banner / F3b stale-fns-banner — the framework's OWN notice vocabulary inside
           the canvas (same tier as .expr-chip: a client-computed marker, not app content). Unstyled plain
           text was indistinguishable from the design's own rendered content (ux review) — a calm, subtle
           treatment (--warn, NOT --danger/red: this is advisory, "go fix it", never a destructive/error
           state) makes clear the TOOL is talking. */
        .design-canvas .eval-degrade-banner, .design-canvas .stale-fns-banner {
          background: color-mix(in srgb, var(--warn) 10%, var(--surface)); border-left: 3px solid var(--warn);
          color: var(--warn); padding: 0.45rem 0.7rem; margin: 0 0 0.5rem; border-radius: 4px; font-size: 0.85rem; }
        /* S6a — the NO-CTX TEMPLATE mode for for/if canvas rows: a for renders its body ONCE behind a
           dashed marker card (echoing the tree editor's dashed .node-for/.node-if); an if renders BOTH
           branches, each labeled then/else — the canvas never guesses which loop iterations or which
           branch would run (that is S6b's row-scope eval). */
        .design-canvas .for-template, .design-canvas .if-template {
          border: 1px dashed var(--border); border-radius: 6px; padding: 0.4rem 0.6rem; margin: 0.3rem 0; }
        .design-canvas .for-badge, .design-canvas .if-badge {
          display: flex; align-items: center; gap: 0.3rem; font-size: 0.78rem; color: var(--muted); margin-bottom: 0.3rem; }
        .design-canvas .for-item { font-family: ui-monospace, monospace; font-weight: 600; }
        .design-canvas .if-branch { margin: 0.2rem 0; }
        .design-canvas .if-branch .branch-label { font-size: 0.72rem; }
        /* M12 S4a — canvas selection chrome. Discoverability (ux review fold): a [selecttarget] canvas
           element gets a pointer cursor and a faint hover outline — the ONLY signal, before any click, that
           the canvas is a selection surface at all. This doubles as the boundary signal for the LOOK-ALIKE
           surfaces that never carry [selecttarget] (a workbench card's static .design-canvas.use-preview
           preview): they simply never get the pointer/hover, so their click no-op reads as "not
           interactive" rather than "broken". `is-selected` marks BOTH sides of a click-to-select pair: any
           canvas element sharing the clicked data-node (a client post-pass, ui.ts applySelectionChrome —
           scoped to any [selecttarget] container, so this rule fires only where that marker exists) gets a
           calm accent outline (NOT --danger — this is a neutral "here" marker, not an error); the mirrored
           tree-editor row (renderNodeEditor's own reactive class, app.deenv nodeClass) is a full-width block
           rather than a thin element, so a soft accent-tinted background reads better there than an outline. */
        [selecttarget] [data-node] { cursor: pointer; }
        [selecttarget] [data-node]:hover { outline: 1px solid color-mix(in srgb, var(--accent) 35%, transparent); outline-offset: 1px; border-radius: 2px; }
        [selecttarget] [data-node].is-selected { outline: 2px solid var(--accent); outline-offset: 1px; border-radius: 2px; }
        .render-tree .is-selected, .fn-body .is-selected {
          background: color-mix(in srgb, var(--accent) 10%, transparent); border-radius: 4px; }
        /* The render group: the CANVAS (live structural view) + the TREE EDITOR it mirrors are ONE
           authoring pair under one heading/divider — the divider must sit ABOVE the pair, never between
           them (a border between the canvas and its own tree visually cut the pair apart — ux review). */
        .render-section { margin: 1.6rem 0 0.4rem; padding-top: 1.2rem; border-top: 1px solid var(--border); }
        .render-heading { font-size: 1.1rem; margin: 0 0 0.2rem; }
        .render-caption { color: var(--muted); font-size: 0.86rem; margin: 0.6rem 0; }
        /* M12 S5b — the palette: an expand-on-demand <details> (native disclosure, no framework toggle state
           needed) sitting between the canvas and the tree it inserts into — NOT a persistent sidebar, the
           page is already long. .palette-target states the insert-at-selection rule up front, before the
           operator picks a name. */
        .component-palette { margin: 0.5rem 0 0.8rem; }
        .palette-toggle { cursor: pointer; font-size: 0.86rem; color: var(--accent); padding: 0.2rem 0; }
        .palette-target { color: var(--muted); font-size: 0.82rem; margin: 0.4rem 0; }
        .palette-group { margin: 0.3rem 0 0.6rem; }
        .palette-group-label { display: block; color: var(--muted); font-size: 0.78rem; text-transform: uppercase;
          letter-spacing: 0.03em; margin: 0.2rem 0; }
        .palette-group-caption { color: var(--muted); font-size: 0.78rem; font-style: italic; margin: 0.1rem 0 0.35rem; }
        .palette-empty { color: var(--muted); font-size: 0.82rem; font-style: italic; }
        .palette-item { display: inline-block; padding: 0.15rem 0.55rem; margin: 0.15rem 0.3rem 0.15rem 0;
          font-size: 0.85rem; }
        .palette-item:hover { background: color-mix(in srgb, var(--accent) 8%, transparent); border-color: var(--accent); }
        /* M12 U1 — a Configurations row (a MetaUse: name + args + its own static preview) needs minimal
           containment: each is a DISCRETE, independent unit on an otherwise-tall component card, so a
           subtle border/background/spacing reads "two configs, two separate things" at a glance. No new
           layout system — the same card treatment `.type-card` already uses, one notch lighter. */
        .use-row { background: var(--surface); border: 1px solid var(--border); border-radius: 8px;
          padding: 0.7rem 0.85rem; margin: 0.5rem 0; }
        .use-preview-label { color: var(--muted); font-size: 0.78rem; font-weight: 600;
          text-transform: uppercase; letter-spacing: .02em; margin: 0.5rem 0 0.2rem; }
        /* M12 W1a — the component workbench mounts a REAL running instance into .use-preview (workbench.ts);
           .workbench-note is the static card-footer line surfacing the v1 fidelity boundary (host actions/
           saves silently no-op under a nulled wsHooks — never silent, per the design doc); .instance-error
           is the real thrown error a broken instance renders (a genuine failure, --danger — not the advisory
           --warn tier the eval-degrade/stale-fns banners use). */
        .workbench-note { color: var(--muted); font-size: 0.76rem; font-style: italic; margin: 0.3rem 0; }
        .instance-error { color: var(--danger); background: color-mix(in srgb, var(--danger) 8%, var(--surface));
          border-left: 3px solid var(--danger); padding: 0.45rem 0.7rem; border-radius: 4px; font-size: 0.85rem; }
        /* M12 W1b — the live-instance driver's OWN control bar (workbench.ts, ensureInstanceContent): a
           Reset button rendered as plain client-runtime DOM inside the `.use-preview` container, never
           through app.deenv markup (app docs have no host-DOM/comment syntax to author this in, and the
           container is driver-owned after mount anyway). `.workbench-instance-content` isolates the
           previewed component's OWN rendered tree from the toolbar, one level deeper, so the reconciler
           can never confuse the Reset button with the component's own root element (e.g. Counter's root
           IS a `<button>`). */
        .workbench-instance-toolbar { display: flex; justify-content: flex-end; align-items: center; gap: 0.25rem; margin-bottom: 0.4rem; }
        .workbench-instance-reset, .workbench-history-back, .workbench-history-fwd { font-size: 0.76rem; padding: 0.15rem 0.5rem; background: var(--surface);
          border: 1px solid var(--border); border-radius: 4px; color: var(--muted); cursor: pointer; }
        .workbench-instance-reset:hover, .workbench-history-back:hover, .workbench-history-fwd:hover { border-color: var(--muted); color: var(--text); }
        .workbench-history-back:disabled, .workbench-history-fwd:disabled { opacity: 0.4; cursor: default; }
        .workbench-history-pos { font-size: 0.72rem; color: var(--muted); min-width: 2.2em; text-align: center; }
        /* Raw code areas tucked behind a disclosure so the type editor reads as just types by default. */
        details.code-areas { margin-top: 1.4rem; border-top: 1px solid var(--border); padding-top: 0.5rem; }
        details.code-areas summary.code-summary { font-weight: 600; color: var(--muted); cursor: pointer; padding: 0.3rem 0; }
        details.code-areas[open] summary.code-summary { margin-bottom: 0.6rem; }
        /* .design-label is a SPAN in the instances list (the resolved design name) and an editable INPUT
           in the editor (the design's renamable label) — bold in both; the editor input also reads as a
           heading-sized field. */
        .design-label, .instance-app, .instance-port { font-weight: 600; }
        .design-editor > input.design-label { display: block; width: 100%; max-width: 440px; font-size: 1.15rem;
          margin: 0 0 0.4rem; }
        /* The designs list is an action-managed <SetTable> (rowActions set → .set-table.managed, no
           whole-row overlay), so its per-row action cell (Edit link + Delete button + the inline delete
           confirm) needs no z-index band-aid — the buttons are directly clickable. Just lay the cell out
           (right-aligned, no wrap, with small gaps between controls). */
        .set-row td.design-actions { width: 1%; white-space: nowrap; text-align: right; }
        .set-row td.design-actions a, .set-row td.design-actions button { margin-left: 0.4rem; }
        .new-instance { display: flex; flex-wrap: wrap; gap: 0.5rem; align-items: end;
          margin: 0.6rem 0 1.2rem; padding: 0.9rem; background: var(--surface); border: 1px solid var(--border); border-radius: 10px; }
        .new-instance input, .new-instance select { max-width: 180px; }
        .design-editor { margin-top: 1rem; }
        /* Per-row overflow ("kebab") menu: ALL of a row's actions (Open/Rename/Clone/Delete) live in
           one trailing actions cell behind a "⋯" toggle, instead of being scattered across columns.
           The menu container (and its click-outside backdrop) are always in the DOM and toggled by the
           .open class (the component flips it) — never conditionally inserted, so the foreach row's
           children stay structurally stable. The OPEN menu is position:absolute so it overlays rather
           than growing the row and reflowing the table; the .row-actions cell (and .kebab) are the
           positioning context. A full-viewport .kebab-backdrop sits just under the menu to catch an
           outside click and close it. */
        .instances-table td.row-actions { position: relative; width: 1%; white-space: nowrap; text-align: right; }
        .kebab { position: relative; display: inline-block; }
        /* The toggle reads as a button at rest (subtle border + surface), not a transparent glyph. */
        .kebab-toggle { position: relative; z-index: 50; padding: 0.15rem 0.55rem; line-height: 1;
          font-weight: 700; letter-spacing: .08em; color: var(--muted);
          background: var(--surface); border: 1px solid var(--border); }
        .kebab-toggle:hover { background: #f3f4f6; color: var(--text); }
        /* Click-outside-to-close: hidden until the menu opens, then a fixed full-viewport catcher
           beneath the menu (z below the menu, above the page). */
        .kebab-backdrop { display: none; }
        .kebab-backdrop.open { display: block; position: fixed; inset: 0; z-index: 40; }
        .kebab-menu { display: none; }
        .kebab-menu.open { display: flex; flex-direction: column; align-items: stretch; gap: 0.15rem;
          position: absolute; top: 100%; right: 0; z-index: 50; min-width: 9rem;
          margin-top: 0.25rem; padding: 0.3rem; background: var(--surface); border: 1px solid var(--border);
          border-radius: 8px; box-shadow: 0 2px 8px rgba(31,35,40,.12); text-align: left; }
        .kebab-menu a, .kebab-menu button { display: block; width: 100%; text-align: left;
          border-color: transparent; background: transparent; }
        .kebab-menu a:hover, .kebab-menu button:hover { background: #f3f4f6; }
        .kebab-menu a.open-instance { padding: 0.4rem 0.85rem; color: var(--text); }
        /* The two-step delete confirm inside the menu (mirrors the designs list): inline "Delete?" + Yes/Cancel. */
        .kebab-menu .delete-confirm { display: block; padding: 0.4rem 0.85rem 0.1rem; font-size: 0.85rem; color: var(--danger); }
        .kebab-menu button.delete-yes { color: var(--danger); }
        /* The detail page's head: name (or inline rename) + the kebab on one line, above the selector. */
        .instance-head { display: flex; flex-wrap: wrap; gap: 0.5rem; align-items: center; margin-bottom: 0.6rem; }

        /* The commit-detail page (/commits/<id>): a read-only field list + the cached canonical snapshot. */
        .commit-detail { margin-top: 1rem; }
        .commit-field { display: flex; gap: 0.6rem; padding: 0.35rem 0; border-bottom: 1px solid var(--border-soft); }
        .commit-field .field-label { flex: 0 0 8rem; color: var(--muted); }
        .commit-field .field-value { min-width: 0; word-break: break-word; }
        .commit-detail .text-label { display: block; margin: 1rem 0 0.35rem; color: var(--muted); }
        .commit-text, .commit-migration-text { margin: 0; padding: 0.8rem; background: var(--surface); border: 1px solid var(--border);
          border-radius: 8px; max-height: 24rem; overflow: auto; white-space: pre-wrap; word-break: break-word;
          font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 0.85rem; }
        /* "Changes since parent" — the structural (identity-based) diff between a commit and its parent. */
        .commit-diff .diff-groups { display: flex; flex-direction: column; gap: 0.7rem; }
        .commit-diff .diff-muted { margin: 0; color: var(--muted); }
        .commit-diff .diff-group { display: flex; flex-direction: column; gap: 0.2rem; }
        .commit-diff .diff-label { color: var(--muted); font-size: 0.85rem; }
        .commit-diff .diff-rename, .commit-diff .diff-add, .commit-diff .diff-remove,
        .commit-diff .diff-convert, .commit-diff .diff-cardinality {
          font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 0.9rem; }
        /* Removes are ALWAYS destructive (red); retypes + cardinality reshapes are POTENTIALLY lossy
           (amber "may lose data") — a scalar convert defaults unconvertible cells, a reshape can drop the
           old value — so they signal caution without crying wolf like an always-safe rename/add. */
        .commit-diff .diff-remove { color: var(--danger); }
        .commit-diff .diff-convert, .commit-diff .diff-cardinality { color: var(--warn); }

        /* Publish section (design editor, M13 Track-B B3) — the dry-run preview of what deploying this design
           onto each running instance WOULD do, surfaced LOUDLY before the Apply. Reuses the diff colour
           language: removes + un-carriable retypes/reshapes are destructive (red), safe renames/adds are
           muted; the drift/up-to-date lines are advisory (amber/muted). */
        .publish-section { margin: 1.6rem 0 0.4rem; padding-top: 1.2rem; border-top: 1px solid var(--border); }
        .publish-heading { margin: 0 0 0.3rem; font-size: 1.1rem; }
        .publish-caption { margin: 0 0 0.8rem; color: var(--muted); font-size: 0.9rem; }
        .publish-empty { margin: 0; color: var(--muted); }
        .publish-row { border: 1px solid var(--border); border-radius: 8px; padding: 0.7rem 0.9rem;
          margin-bottom: 0.7rem; background: var(--surface); }
        .publish-row-head { display: flex; align-items: center; gap: 0.7rem; }
        .publish-target { font-weight: 600; margin-right: auto; }
        .publish-preview { margin-top: 0.8rem; }
        .publish-report { display: flex; flex-direction: column; gap: 0.7rem; }
        .publish-uptodate { margin: 0; color: var(--muted); }
        .publish-note { margin: 0; color: var(--muted); font-size: 0.9rem; }
        .publish-drift { margin: 0; color: var(--warn); font-size: 0.9rem; }
        .publish-group { display: flex; flex-direction: column; gap: 0.2rem; }
        .publish-label { color: var(--muted); font-size: 0.85rem; }
        .publish-remove, .publish-convert, .publish-cardinality, .publish-rename, .publish-add {
          font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 0.9rem; }
        /* Destructive LOUDLY (red): removes always, and retypes/reshapes flagged lossy (is-lossy). A
           non-lossy retype/reshape stays amber caution; safe rename/add stay neutral text. */
        .publish-remove { color: var(--danger); }
        .publish-convert, .publish-cardinality { color: var(--warn); }
        .publish-convert.is-lossy, .publish-cardinality.is-lossy { color: var(--danger); }
        .publish-lossy-note { color: var(--danger); }
        /* Apply sits BELOW the loud warnings — destructive-op-first. A destructive report routes Apply
           through the two-step ConfirmButton (the same delete pattern); its trigger reads as dangerous. */
        .publish-preview .apply-publish { margin-top: 0.7rem; }
        .publish-preview .confirm-button { display: inline-block; margin-top: 0.7rem; }
        .publish-preview .apply-publish.is-destructive { color: var(--danger); border-color: var(--danger); }
        .publish-preview .apply-publish.is-destructive:hover { background: #fff0f0; }
        /* The confirm STEP itself must carry the same danger colour as its trigger (review fix) — otherwise
           the loudness stops right before the actual point-of-no-return. Mirrors the kebab's own rules. */
        .publish-preview .delete-confirm { color: var(--danger); }
        .publish-preview button.delete-yes { color: var(--danger); border-color: var(--danger); }

        /* Branches + merge (M13 Track-B B4). The design editor's Branches section: a create-branch box,
           the branch links (each a navigation to that branch's own /designs/<id> URL), then a toggle-gated
           merge preview reusing the publish-report shapes. */
        .branch-section { margin: 1.6rem 0 0.4rem; padding-top: 1.2rem; border-top: 1px solid var(--border); }
        .branch-heading { margin: 0 0 0.3rem; font-size: 1.1rem; }
        .branch-caption { margin: 0 0 0.8rem; color: var(--muted); font-size: 0.9rem; }
        .branch-create { display: flex; gap: 0.5rem; align-items: center; margin-bottom: 0.9rem; }
        .branch-create input.branch-name { max-width: 220px; }
        .branch-list { display: flex; flex-direction: column; gap: 0.4rem; }
        .branch-empty { margin: 0; color: var(--muted); }
        .branch-row { border: 1px solid var(--border); border-radius: 8px; padding: 0.6rem 0.8rem;
          display: flex; flex-direction: column; gap: 0.5rem; }
        .branch-row-head { display: flex; align-items: center; gap: 0.7rem; }
        .branch-link { font-weight: 600; margin-right: auto; }
        .branch-current { color: var(--muted); font-size: 0.85rem; }

        .merge-preview { margin-top: 0.5rem; }
        .merge-report { display: flex; flex-direction: column; gap: 0.7rem; }
        .merge-clean { margin: 0; color: var(--muted); }
        .merge-uptodate { margin: 0; color: var(--muted); }
        /* Drift is loud (the merge cannot proceed until the side is committed). */
        .merge-drift { margin: 0; color: var(--warn); font-size: 0.9rem; }
        /* Access changes are a MUST-SEE security surface — always shown, loud like a destructive publish. */
        .merge-access { border: 1px solid var(--warn); border-radius: 8px; padding: 0.5rem 0.7rem; }
        .merge-access-label { color: var(--warn); font-weight: 600; font-size: 0.85rem; }
        .merge-access-row { color: var(--warn); font-family: var(--mono, monospace); font-size: 0.85rem; }
        .merge-conflicts { display: flex; flex-direction: column; gap: 0.6rem; }
        .merge-conflict { border: 1px solid var(--danger); border-radius: 8px; padding: 0.5rem 0.7rem;
          display: flex; flex-direction: column; gap: 0.3rem; }
        .merge-conflict-head { color: var(--danger); font-weight: 600; font-size: 0.9rem; }
        .merge-conflict-vals { display: flex; flex-direction: column; gap: 0.1rem;
          font-family: var(--mono, monospace); font-size: 0.85rem; }
        .merge-conflict-picks { display: flex; gap: 0.5rem; margin-top: 0.2rem; }
        .merge-conflict-picks button.is-picked { background: var(--accent); border-color: var(--accent); color: #fff; }
        .merge-apply { margin-top: 0.3rem; }
        .merge-apply:disabled { opacity: 0.5; cursor: not-allowed; }

        /* Todo showcase (the committed default app — a custom fn render composing the library).
           These rules are the todo's own LAYOUT only (cards, grid, row arrangement, widths); the
           components' visual appearance lives in the component rules above, never re-skinned here. */
        .todo-app .user-bar { display: flex; flex-wrap: wrap; gap: 0.5rem; align-items: center;
          padding: 0.8rem; margin-bottom: 1.4rem; background: var(--surface);
          border: 1px solid var(--border); border-radius: 10px; }
        .user-chip { border-radius: 100px; padding: 0.35rem 0.85rem; }
        .user-chip:not(.selected):hover { border-color: var(--accent); color: var(--accent); }
        .user-chip.selected { background: var(--accent); border-color: var(--accent); color: #fff; }
        .user-bar input.new-user { max-width: 160px; }
        .selected-user { margin-top: 0; }
        .user-lists .add-list { display: flex; gap: 0.5rem; align-items: center; margin: 0.4rem 0 1.2rem; }
        .user-lists .add-list input.new-list { max-width: 220px; }
        .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 1rem; }
        .todo-card { background: var(--surface); border: 1px solid var(--border); border-radius: 10px;
          padding: 1rem 1.1rem; box-shadow: 0 1px 2px rgba(31,35,40,.05); }
        .todo-card .list-name { margin: 0 0 0.6rem; color: var(--text); font-size: 1.05rem; }
        .checklist { list-style: none; margin: 0 0 0.7rem; padding: 0; }
        .item-row { display: flex; align-items: center; gap: 0.5rem; padding: 0.3rem 0;
          border-bottom: 1px solid var(--border-soft); }
        .item-row:last-child { border-bottom: none; }
        .item-row input.text { flex: 1; min-width: 0; }
        .item-row input.checked:checked + input.text { color: var(--muted); text-decoration: line-through; }
        .item-row .remove-item { opacity: 0; transition: opacity .12s; padding: 0.05rem 0.4rem; font-size: 1.05rem;
          line-height: 1; border-color: transparent; background: transparent; color: var(--muted); }
        .item-row:hover .remove-item, .item-row .remove-item:focus-visible { opacity: 1; }
        .item-row .remove-item:hover { color: var(--danger); background: #fff0f0; border-color: var(--danger); }
        .todo-card .add-item { display: flex; gap: 0.4rem; margin-top: 0.5rem; }
        .todo-card .add-item input.new-item { flex: 1; min-width: 0; }

        /* Auth chrome: the sign-in / user menu sits on the breadcrumb ROW, right-aligned to the content
           column. #app is the positioned, max-width-centered ancestor (so right:0 is the column edge); the
           negative top lifts the bar up onto the breadcrumb line above #app (whose height is the breadcrumb's
           0.9rem line + 1.2rem margin). The sign-in login form drops DOWN from the bar (absolute, top:100%)
           so opening it never disturbs the bar's row; user management is a "Users" link to the generic list
           (/users), not a drop-down panel. */
        #app { position: relative; }
        .app-shell > .sign-in-bar, .app-shell > .user-menu {
          position: absolute; top: -2.4rem; right: 0; margin: 0;
          display: flex; align-items: center; gap: 0.6rem; font-size: 0.9rem; }
        .user-menu .user-name { color: var(--muted); }
        .sign-in-bar button.sign-in, .user-menu button.logout, .user-menu a.manage-users {
          padding: 0.25rem 0.65rem; font-size: 0.85rem; }
        .user-menu a.manage-users {
          border: 1px solid var(--border); border-radius: 6px; background: var(--surface);
          color: var(--text); text-decoration: none; }
        .sign-in-bar .login-form {
          position: absolute; top: 100%; right: 0; margin: 0.45rem 0 0; z-index: 20; text-align: left;
          background: var(--surface); border: 1px solid var(--border); border-radius: 10px;
          padding: 1rem 1.15rem; box-shadow: 0 6px 22px rgba(31,35,40,.14); min-width: 240px; }
        """;

    private static void DefineFunction(CodeFunction fn, ExecScope scope)
    {
        if (fn.Name == null) return;
        scope.Items[fn.Name] = new ExecScopeItem
        {
            Value = new ExecFunction { Function = fn, Scope = scope },
            IsReadOnly = true,
        };
    }

    // HTML void elements: rendered without a closing tag or children.
    private static readonly HashSet<string> VoidElements =
        ["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"];

    // `selectValue` is the bound value of an enclosing <select> (null otherwise), threaded so an
    // <option> can mark itself `selected` when its own `value` matches the selection — the SSR half
    // of <select> two-way binding (see SerializeTag's select branch).
    private void SerializeChild(IExecTagChild child, StringBuilder sb, string @base, IExecValue? selectValue = null)
    {
        switch (child)
        {
            case ExecTag tag:
                SerializeTag(tag, sb, @base, selectValue);
                break;
            case ExecArray coll:
                // foreach / where / orderBy flatten into the child stream (e.g. a select's options
                // built by foreach), so the select's value carries through the flattening.
                foreach (var item in coll.Items) SerializeChild(item.Value, sb, @base, selectValue);
                break;
            case ExecText text:
                sb.Append(Escape(text.Value));
                break;
            case ExecInt i:
                sb.Append(Escape(i.Value.ToString(CultureInfo.InvariantCulture)));
                break;
            case ExecBool b:
                sb.Append(b.Value ? "true" : "false");
                break;
            // null / nothing / functions render nothing.
        }
    }

    private void SerializeTag(ExecTag tag, StringBuilder sb, string @base, IExecValue? selectValue = null)
    {
        // A <textarea>'s value is its TEXT CONTENT, not a `value` attribute (browsers ignore
        // `value` on <textarea>), so its bound `value` is emitted as escaped content below and
        // skipped in the attribute loop. The client twin (ui.ts) mirrors this: it sets the
        // .value property, never the attribute.
        var isTextarea = tag.Name == "textarea";

        // A <select value={x}> drives which <option> is `selected` — `value` is not real HTML on a
        // <select> (the browser ignores it), so it is skipped in the attribute loop and instead passed
        // down to the option children. An <option> whose own `value` equals the selection gets `selected`.
        var isSelect = tag.Name == "select";
        // The selection threads from <select value={x}> down to its <option>s — including through an
        // <optgroup>, which is a transparent grouping container (it inherits the selection so a grouped
        // option still marks itself `selected`). Any other tag stops the thread.
        var childSelectValue =
            isSelect && tag.Attributes.TryGetValue("value", out var selVal) ? selVal
            : tag.Name == "optgroup" ? selectValue
            : null;
        var isSelectedOption =
            tag.Name == "option" && selectValue != null
            && tag.Attributes.TryGetValue("value", out var optVal) && ScalarsEqual(optVal, selectValue);

        sb.Append('<').Append(tag.Name);
        foreach (var (name, value) in tag.Attributes)
        {
            if ((isTextarea || isSelect) && name == "value") continue;
            AppendCodeAttribute(sb, name, value, @base);
        }
        if (isSelectedOption) sb.Append(" selected");
        sb.Append('>');

        if (VoidElements.Contains(tag.Name)) return;

        // The bound value as the first content (HTML-escaped, same scalar handling as
        // AppendCodeAttribute/SerializeChild), then any literal children.
        if (isTextarea && tag.Attributes.TryGetValue("value", out var v))
            AppendTextareaValue(sb, v);

        foreach (var child in tag.Children) SerializeChild(child, sb, @base, childSelectValue);
        sb.Append("</").Append(tag.Name).Append('>');
    }

    // Equality for two scalar exec values, used to match an <option>'s value against the enclosing
    // <select>'s selection. Cross-type int/text compare by their canonical text (an int option value
    // authored as a number must match a text-typed selection that holds its digits, and vice versa) —
    // the same lenient text coercion the client uses (option.value is always a DOM string).
    private static bool ScalarsEqual(IExecValue a, IExecValue b) =>
        ScalarText(a) is { } at && ScalarText(b) is { } bt && at == bt;

    private static string? ScalarText(IExecValue v) => v switch
    {
        ExecText t => t.Value,
        ExecInt i => i.Value.ToString(CultureInfo.InvariantCulture),
        ExecBool b => b.Value ? "true" : "false",
        _ => null,
    };

    // The scalar value bound to a <textarea>, as HTML-escaped element content. Non-scalar
    // values (an unset/null bind) render nothing; the bool case is unreachable for a value
    // bind but handled for completeness, matching SerializeChild.
    private static void AppendTextareaValue(StringBuilder sb, IExecValue value)
    {
        switch (value)
        {
            case ExecText t:
                sb.Append(Escape(t.Value));
                break;
            case ExecInt i:
                sb.Append(Escape(i.Value.ToString(CultureInfo.InvariantCulture)));
                break;
        }
    }

    // Only scalar attribute values become HTML attributes. Function values (event
    // handlers like onClick) and objects/arrays/null are wired on the client, not
    // serialized. A bool attribute follows HTML semantics: present iff true.
    //
    // A navigational URL attribute (href/src) whose value is ROOT-RELATIVE is prefixed with the
    // mount `base` HERE, at the edge — so app Code keeps writing `/notes/2` (mount-unaware) and the
    // emitted link is mount-correct (`/apps/todo/notes/2`). The client reconciler (ui.ts) applies
    // the same prefix, so SSR and hydrate agree. With base "/" this is an identity (behavior-preserving).
    //
    // Two XSS guards live here, at the single chokepoint every attribute routes through (the client
    // twin is ui.ts's refreshAttributes):
    //  - An `on*` event-attribute name (onclick, onmouseover, …) with a SCALAR value is dropped
    //    entirely — a real handler is always a `fn` value (never reaches this method's switch; an
    //    ExecFunction falls through and already emits nothing), so a scalar there can only be an
    //    injection (`<div onclick={db.evil}>`). Checked before the type switch so it applies to every
    //    scalar kind (text/int/bool), not just the text case.
    //  - A url attribute (href/src) is scheme-checked AFTER MountUrl: a mount-prefixed root-relative
    //    path never carries a scheme, so only a raw app-supplied absolute value (`javascript:…`) can
    //    trip it. HtmlEncode does not neutralize a dangerous scheme (no special characters to escape),
    //    so the attribute is dropped outright rather than encoded — a link with no href is inert.
    private static void AppendCodeAttribute(StringBuilder sb, string name, IExecValue value, string @base)
    {
        if (IsEventAttribute(name)) return;

        switch (value)
        {
            case ExecText t:
                var text = IsUrlAttribute(name) ? MountUrl(@base, t.Value) : t.Value;
                if (IsUrlAttribute(name) && HasDangerousScheme(text)) return;
                sb.Append(' ').Append(name).Append("=\"").Append(Escape(text)).Append('"');
                break;
            case ExecInt i:
                sb.Append(' ').Append(name).Append("=\"").Append(i.Value.ToString(CultureInfo.InvariantCulture)).Append('"');
                break;
            case ExecBool b:
                if (b.Value) sb.Append(' ').Append(name);
                break;
        }
    }

    // Build the read-only `instances` Code collection from the registry snapshot: one row per
    // hosted instance, { id, app, path, designId } scalars. A transient List (negative ids), like a
    // where/orderBy result — an app reads the rows in output position, so they ship as leaves and the
    // list survives hydration. Empty when there is no kernel. `id` is the host-action address (e.g.
    // sys.publish(db, i.id)), unique per instance and the sole key to its files; `app` is the display
    // name (which also determines the mount); `path` is `/apps/<name>` — the instance's address, now
    // that hosting is by path (no per-instance ports); `designId` is the explicit reference to the IDE
    // design this instance runs (0 = none). clone/delete/publish work on ANY instance, so there is no
    // created/boot flag.
    private ExecArray BuildRegistry(ExecContext context)
    {
        var items = new List<ExecItem>();
        foreach (var info in _registry.Current)
            items.Add(new ExecItem
            {
                Key = --context.LastId.Value,
                Value = new ExecObject
                {
                    Id = --context.LastId.Value,
                    Props = new Dictionary<string, IExecValue>
                    {
                        ["id"] = new ExecInt { Value = info.Id },
                        ["app"] = new ExecText { Value = info.App },
                        ["path"] = new ExecText { Value = info.Path },
                        ["designId"] = new ExecInt { Value = info.DesignId ?? 0 },
                    },
                },
            });
        return new ExecArray { Items = items, Id = --context.LastId.Value, Kind = ArrayKind.List };
    }

    // The bound principal as a scalar-only ExecObject (M-auth) — see AccessFloor.LoadPrincipal, the single
    // source of truth shared with the WsHandler write floor so both decide over an identical principal.
    private IExecValue LoadPrincipal(int? principalUserId) =>
        AccessFloor.LoadPrincipal(_store, _desc, principalUserId);

    // ── sys.diffCommits (M13 Track-B B2) ────────────────────────────────────────

    // The compute the CodeExecutor's `sys.diffCommits(from, to)` delegates to (threaded in above). Lives HERE,
    // not in the Code layer, because it bridges to DesignDiffer (in DeEnv.Designer, which the interpreter core
    // deliberately never references) — SsrRenderer is where both Designer and Code are visible. Traffics only
    // Code-layer types: two commit ExecObjects + the render context (to mint transient ids), a Code report
    // value out. The executor caches the returned report DIRECTLY under `diffCommits:{from}:{to}` (like
    // sys.schema — Memoize's factory guard would refuse a fresh negative-id object), and that cache entry
    // ships it to the client whole.
    //
    // Reads each commit object's cached `text` (an ExecText) and `idMap` (a Kind=Dict ExecArray of int-by-text
    // entries) off its props, rebuilds each DesignSnapshot, and runs the rename-aware DesignDiffer. The report
    // mirrors PublishReport's vocabulary (renames/adds/removes/conversions/cardinality) but omits the
    // publish-only boundary-apply fields — a pure commit-to-commit diff has no apply result. Every node is
    // marked Constant so ClientState ships the WHOLE tree (nested arrays and their items), never privacy-
    // filtered to empty: the report is provably user-data-free structural metadata. Each object/array is
    // minted with a DISTINCT transient (negative) id off context.LastId — exactly like an evaluated object
    // literal — so ClientState's identity-dedup (seenObjects/seenArrays) ships each node once, not collapsing
    // the whole tree onto a single shared id=0.
    private static IExecValue BuildCommitDiffReport(ExecObject from, ExecObject to, ExecContext context)
    {
        var diff = DesignDiffer.Compute(SnapshotOf(from), SnapshotOf(to));

        // Local constructors that mint a distinct negative id and stamp Constant, so the shipped tree arrives
        // complete AND uniquely-identified on the client (see the id-dedup note above).
        ExecText T(string v) => new() { Value = v };
        ExecArray Arr(IEnumerable<IExecValue> items)
        {
            var list = items.ToList();
            return new ExecArray
            {
                Id = --context.LastId.Value, Kind = ArrayKind.List, Constant = true,
                Items = [.. list.Select((v, i) => new ExecItem { Key = i, Value = v })],
            };
        }
        ExecObject Obj(params (string Name, IExecValue Value)[] props) =>
            new() { Id = --context.LastId.Value, Constant = true, Props = props.ToDictionary(p => p.Name, p => p.Value) };

        // renames: type renames (from=FromName, to=ToName) ++ prop renames (from="Type.fromProp", to="Type.toProp").
        var renames = Arr([
            .. diff.TypeRenames.Select(r => (IExecValue)Obj(("from", T(r.FromName)), ("to", T(r.ToName)))),
            .. diff.PropRenames.Select(r => (IExecValue)Obj(
                ("from", T($"{r.TypeName}.{r.FromProp}")), ("to", T($"{r.TypeName}.{r.ToProp}")))),
        ]);
        // adds: bare path strings "Type" or "Type.prop", exactly like PublishReport.Adds.
        var adds = Arr([
            .. diff.TypeAdds.Select(a => (IExecValue)T(a.TypeName)),
            .. diff.Adds.Select(a => (IExecValue)T($"{a.TypeName}.{a.PropName}")),
        ]);
        // removes: prop removes "Type.prop" ++ whole-type removes "Type" — bare path strings.
        var removes = Arr([
            .. diff.Removes.Select(r => (IExecValue)T($"{r.TypeName}.{r.PropName}")),
            .. diff.TypeRemoves.Select(r => (IExecValue)T(r.TypeName)),
        ]);
        // conversions: { path, from, to } — a scalar type change.
        var conversions = Arr(diff.Conversions.Select(c => (IExecValue)Obj(
            ("path", T($"{c.TypeName}.{c.PropName}")), ("from", T(c.FromType)), ("to", T(c.ToType)))));
        // cardinality: { path, from, to } — a single/set/dictionary reshape (Cardinality.ToString()).
        var cardinality = Arr(diff.CardinalityChanges.Select(c => (IExecValue)Obj(
            ("path", T($"{c.TypeName}.{c.PropName}")),
            ("from", T(c.FromCardinality.ToString())), ("to", T(c.ToCardinality.ToString())))));

        return Obj(
            ("isEmpty", new ExecBool { Value = diff.IsEmpty }),
            ("renames", renames),
            ("adds", adds),
            ("removes", removes),
            ("conversions", conversions),
            ("cardinality", cardinality));
    }

    // Rebuild a commit's DesignSnapshot (text + name-path→id map) from the props shipped on the commit object:
    // `text` is a plain ExecText; `idMap` is a Kind=Dict ExecArray whose entries are scalar dict rows — each an
    // entry object carrying its name-path in the reserved `__key` field and its int id in `value` (see
    // DbBridge's dictionary materialization).
    private static DesignSnapshot SnapshotOf(ExecObject commit)
    {
        var text = commit.Props.TryGetValue("text", out var t) && t is ExecText et ? et.Value
            : throw new CodeRuntimeException("A commit passed to diffCommits() has no text snapshot.");
        var idMap = new Dictionary<string, int>();
        if (commit.Props.TryGetValue("idMap", out var m) && m is ExecArray { Kind: ArrayKind.Dict } arr)
            foreach (var item in arr.Items)
                if (item.Value is ExecObject entry
                    && entry.Props.TryGetValue("__key", out var k) && k is ExecText key
                    && entry.Props.TryGetValue("value", out var v) && v is ExecInt id)
                    idMap[key.Value] = id.Value;
        return new DesignSnapshot(text, idMap);
    }

    // The compute the CodeExecutor's `sys.mergePreview(source, target)` delegates to (threaded in above), the
    // read-side sibling of `sys.mergeBranch` (M13 Track-B B4). SELF-BUILT here (unlike B3's kernel-wired
    // publish preview) because a merge is entirely between two DESIGN rows in the designer's OWN store — the
    // same store this renderer already holds — with NO cross-instance/kernel data. Lives HERE, not in the Code
    // layer, because it bridges to KernelHostActions.ComputeMergePlan (the shared merge core, in DeEnv.Kernel,
    // which the interpreter core never references). Traffics only Code-layer types: the two design ExecObjects
    // + the render context (to mint the report's transient ids), a Code report value out. The executor caches
    // the returned report DIRECTLY under `mergePreview:{source}:{target}` (like diffCommits — Memoize's
    // factory guard would refuse a fresh negative-id object), and that cache entry ships it to the client whole.
    //
    // Runs the SAME ComputeMergePlan sys.mergeBranch runs (drift/no-op/conflict decision + the clean
    // computation), MINUS the apply write — so the preview an operator approves is byte-identical to what the
    // apply would do. resolutions default EMPTY: the first preview shows every conflict; the editor accumulates
    // the operator's per-conflict picks client-side and passes them to sys.mergeBranch on Apply (it does NOT
    // re-preview with them — the picks ARE the resolution). MergeReportCode renders it as a Constant,
    // distinct-negative-id tree so ClientState ships the whole structure.
    private IExecValue BuildMergePreviewReport(ExecObject source, ExecObject target, ExecContext context)
    {
        // Both branches are Design rows in the designer's own store; the clean apply's writer type is
        // JsonFileInstanceStore, which every kernel-hosted store IS (the same cast MergeBranch does).
        if (_store is not JsonFileInstanceStore store)
            throw new CodeRuntimeException("mergePreview() requires the designer's file-backed store.");
        var plan = KernelHostActions.ComputeMergePlan(store, source.Id, target.Id, DesignMerger.NoResolutions);
        return MergeReportCode.Build(plan.Report, context);
    }

    // ── sys.evalContext (M12 CANVAS-EVAL-1) ─────────────────────────────────────

    // The compute `sys.evalContext(design[, refreshKey])` delegates to (threaded into the render executor).
    // SELF-BUILT here (like mergePreview) — a design is a row in the designer's OWN store, and the seed needs
    // only the design node + its own `initialData` (no kernel/cross-instance data). Ships the payload the
    // canvas walk consumes: ONE Constant ExecObject { db, exprs, ambients, params }, all-Constant so
    // ClientState ships the WHOLE tree (the seed graph + the AST map) to the client, which evaluates the
    // canvas identically.
    //   • db    — the design's `initialData` seeded into a THROWAWAY file-backed store, read back via the SAME
    //             DbBridge.LoadRoot that binds live `db`, then RE-MINTED with distinct NEGATIVE ids + Constant
    //             (RemintConstant): the client registers memo-result nodes by id in a GLOBAL registry, so a
    //             positive id would collide with the designer's live data. Shared/cyclic structure is
    //             preserved (a seen-map, mirroring LoadRoot's `loaded`).
    //   • exprs — a content-addressed source-text → { text, ast } map: every render-tree leaf `expr` / attr
    //             `value` source that PARSES, its AST serialized to a JSON string (SchemaJson.Options — the
    //             same wire format the app render trusts). An unparseable source gets no entry (it chips).
    //   • fns   — (M12 F3) a NAME → { ast, fp } map: each design fn's projected CodeFunction (desc.Ui.
    //             Functions — the SAME F1 projection ProjectDesignDb already ran to build `appDb`,
    //             reused rather than re-projected) serialized the SAME wire format as `exprs`' ast (CodeFunction
    //             IS in the ICodeValue union, discriminator "fn"), plus a per-fn CONTENT FINGERPRINT
    //             (SchemaBridge.FnFingerprints — a name/params/body-tree canonical walk over the RAW store
    //             rows, not a hash) the canvas walk compares against the LIVE `fns` rows to show the "stale
    //             call values" banner (F3b) when a fn body has been edited since this ctx was shipped.
    //             EvaluateCtxExpr binds each into the eval scope as a callable, so a call-position expression
    //             (`{fmtDate(n.at)}`) evaluates with the real interpreter.
    //   • libNames — (M12 S5b) a sorted plain ARRAY of `lib`'s own keys, so app Code can `foreach` over it
    //             (the palette's Library group) — `lib` itself is a name-keyed object, and Code has no
    //             generic key-enumeration over an object's props.
    //   • ambients / params — reserved-empty in v1 (the uses/S6/params follow-ups fill them).
    // An INVALID design (projection/load throws) degrades to an EMPTY payload — never a thrown exception that
    // would break the whole canvas: the walk then renders every expr as its chip (honest), and the STRUCTURAL
    // canvas still paints. The temp seed dir is deleted in a finally.
    internal IExecValue BuildEvalContext(ExecObject design, ExecContext context)
    {
        if (_store is not JsonFileInstanceStore)
            throw new CodeRuntimeException("evalContext() requires the designer's file-backed store.");
        ExecObject Obj(params (string Name, IExecValue Value)[] props) =>
            new() { Id = --context.LastId.Value, Constant = true, Props = props.ToDictionary(p => p.Name, p => p.Value) };
        var empty = Obj();
        ExecArray EmptyArr() => new() { Items = [], Id = --context.LastId.Value, Kind = ArrayKind.List };
        // M12 S5b review fold #4, widened — the Library group's OWN shape filter (ui-arch's open
        // question, adjudicated, then the "bare single return" predicate widened once it excluded the
        // library's own flagship components): include a lib fn when EVERY return path of the relevant
        // body provably yields an element — pure structural reflection over its own AST, nothing
        // registers, a component simply looks like one (the foreclosure guard's spirit). The relevant
        // body: the fn's own, OR — for the stateful `var…; fn render(){…}; return render` setup/view
        // idiom (the same shape SchemaBridge.TryMatchStatefulShape recognizes for import, minus import's
        // extra "no other statements" constraint: a helper fn elsewhere in the body, e.g. ConfirmButton's
        // doConfirm, never changes what the fn eventually returns) — the nested render()'s own body.
        // CollectReturnPaths walks that body IGNORING var/fn/assign/call/ambient statements (they don't
        // return) and RECURSING into every if/else-if chain (a statement-level CodeIf, not the tag-child
        // CodeTagIf a JSX children list uses — those live INSIDE an already-found CodeTag's Children and
        // are never visited as separate statements) to collect every reachable `return`. Zero returns, or
        // ANY return whose value is not a literal CodeTag (or a ternary whose own arms all recursively
        // qualify) → excluded: a `return someSymbol` (the library's own top router: `return view`) or a
        // `return someCall()` (route()'s `return NotFoundForm()`) is NOT structurally provable without
        // tracing INTO that symbol/call — exactly the interprocedural reasoning this fn-local, per-fn
        // rule deliberately does not do. A scalar return (InputType/boolGlyph) fails outright.
        static bool ComponentReturnsElement(CodeFunction fn)
        {
            var nestedRender = fn.Body.Statements.OfType<CodeFunction>()
                .FirstOrDefault(f => f.Name == "render" && f.Params.Length == 0);
            var bodyToJudge = nestedRender != null
                && fn.Body.Statements is [.., CodeReturn { Value: CodeSymbol { Name: "render" } }]
                ? nestedRender.Body.Statements
                : fn.Body.Statements;

            var returns = new List<CodeReturn>();
            CollectReturnPathsAll(bodyToJudge, returns);
            return returns.Count > 0 && returns.All(r => ReturnValueIsElement(r.Value));
        }

        static void CollectReturnPathsAll(IEnumerable<ICodeStatement> statements, List<CodeReturn> into)
        {
            foreach (var statement in statements) CollectReturnPathsOne(statement, into);
        }

        static void CollectReturnPathsOne(ICodeStatement statement, List<CodeReturn> into)
        {
            switch (statement)
            {
                case CodeReturn r: into.Add(r); break;
                case CodeBlock b: CollectReturnPathsAll(b.Statements, into); break;
                case CodeIf i:
                    CollectReturnPathsOne(i.Body, into);
                    if (i.ElseBody != null) CollectReturnPathsOne(i.ElseBody, into);
                    break;
                // CodeFunction (a nested helper decl — its OWN returns belong to IT, not this walk),
                // CodeVarDec, CodeAssignment, CodeCall, CodeAmbient: none of these return a value here.
            }
        }

        static bool ReturnValueIsElement(ICodeValue value) => value switch
        {
            CodeTag => true,
            CodeTernary t => ReturnValueIsElement(t.Then) && ReturnValueIsElement(t.Else),
            _ => false,
        };
        try
        {
            var designNode = _store.ReadNode(NodePath.Root.Field("designs").Key(design.Id.ToString())) as ObjectValue
                ?? throw new CodeRuntimeException($"No design with id {design.Id}.");
            // Project (validates — throws SchemaValidationException on an invalid design) → load → seed graph.
            var appDb = SchemaBridge.ProjectDesignDb(designNode);
            var desc = InstanceDescriptionLoader.Load(appDb);
            var seedDb = LoadSeedGraph(desc, context);
            // exprs: every parseable render-tree leaf/attr source → { text, ast:serialized-JSON }.
            var exprs = new Dictionary<string, IExecValue>();
            foreach (var text in SchemaBridge.RenderExprSources(designNode))
            {
                if (exprs.ContainsKey(text)) continue;
                try
                {
                    var ast = CodeParse.ParseExpression(text);
                    var json = JsonSerializer.Serialize(ast, SchemaJson.Options);
                    exprs[text] = Obj(("text", new ExecText { Value = text }), ("ast", new ExecText { Value = json }));
                }
                catch { /* unparseable → no entry (the walk chips it) */ }
            }
            var exprsObj = new ExecObject { Id = --context.LastId.Value, Constant = true, Props = exprs };
            // fns: each design fn (already projected by ProjectDesignDb above, reused here) → its
            // CodeFunction AST + a raw-row content fingerprint (M12 F3 / F3b).
            var fingerprints = SchemaBridge.FnFingerprints(designNode);
            var fns = new Dictionary<string, IExecValue>();
            foreach (var fn in desc.Ui?.Functions ?? [])
            {
                if (fn.Name is not { Length: > 0 }) continue;
                var json = JsonSerializer.Serialize<ICodeValue>(fn, SchemaJson.Options);
                fns[fn.Name] = Obj(
                    ("ast", new ExecText { Value = json }),
                    ("fp", new ExecText { Value = fingerprints.GetValueOrDefault(fn.Name, "") }));
            }
            var fnsObj = new ExecObject { Id = --context.LastId.Value, Constant = true, Props = fns };

            // types / lib (M12 W1c — the workbench sandbox's cache-seeding fast-follow, docs/plans/
            // component-workbench.md "The v1 fidelity boundary"): a fresh private cache always misses every
            // store-backed builtin (sys.schema/sys.extent/sys.new/sys.canWrite/sys.canRead), so a component
            // composing the generic-pattern library (SetTable/ObjectForm/…) rendered its real error card
            // instead of its real UI. GenericUi.Effective(desc) already builds BOTH the pure-data descriptor
            // literals (`Descriptors`, the SAME shape ExecuteSchema/PrewarmDescriptors evaluate for a live
            // page's `schema:*` cache) and the library's own function set (`SystemNames`, the StdlibSource
            // components — the SAME library every app's page ships regardless of design, since Effective
            // always synthesizes it) — reused here rather than recomputed, one call.
            var effective = GenericUi.Effective(desc);

            // types: every declared type's (and dict-prop's) descriptor, EVALUATED + Constant — byte-
            // identical to a live page's `schema:*` entries (the same literal, the same fresh-empty-scope
            // eval, the same MarkConstant). A bare CodeExecutor (no store/floor) evaluates each literal: a
            // descriptor is pure schema data, reading no variables — ExecuteSchema's own invariant. The
            // workbench driver (workbench.ts) seeds its private cache's `schema:`/`canWrite:`/`canRead:`
            // entries straight from this map (`canWrite`/`canRead` are computed CLIENT-SIDE as an
            // unconditional true — the sandbox has no access floor to evaluate; see workbench.ts's own
            // comment for why that is honest, not a shortcut).
            var typeEvaluator = new CodeExecutor();
            var types = new Dictionary<string, IExecValue>();
            foreach (var (name, literal) in effective.Descriptors)
                types[name] = CodeExecutor.MarkConstant(typeEvaluator.ExecuteValue(literal, new ExecScope(), context));
            var typesObj = new ExecObject { Id = --context.LastId.Value, Constant = true, Props = types };

            // lib: the STANDARD LIBRARY's own top-level functions (SetTable/ObjectForm/Field/RefSelect/…),
            // shaped and keyed EXACTLY like `fns` ({ast}) — the SAME bindCtxFns/EvaluateCtxExpr binder
            // already reads. The workbench driver binds `lib` into its sandbox scope FIRST, then `fns`
            // (design fns) — a same-named design fn shadows a library one, mirroring how a real app's own
            // scope nests inside the library scope. `effective.SystemNames` is exactly the library's
            // top-level names (nested helpers inside a component's own body are not separately named here,
            // same as a live page never binds them by name either).
            var lib = new Dictionary<string, IExecValue>();
            foreach (var fn in effective.Ui?.Functions ?? [])
            {
                if (fn.Name is not { Length: > 0 } || !effective.SystemNames.Contains(fn.Name)) continue;
                var libJson = JsonSerializer.Serialize<ICodeValue>(fn, SchemaJson.Options);
                lib[fn.Name] = Obj(("ast", new ExecText { Value = libJson }));
            }
            var libObj = new ExecObject { Id = --context.LastId.Value, Constant = true, Props = lib };

            // libNames (M12 S5b — the palette's Library group): NOT every name in `lib` (which stays the
            // full callable set — any design's own code may call boolGlyph/InputType/route… by name) — a
            // STRUCTURALLY FILTERED subset (ComponentReturnsElement, above), reshaped into a plain sorted
            // ARRAY the app's own deenv code can `foreach` over. `lib` itself is a name-keyed ExecObject and
            // the language has no dict/keys() enumeration over an arbitrary object's props (confirmed at
            // build time — `where`/`orderBy`/`any`/`single`/`add`/`remove` is the whole collection-method
            // surface, all COLLECTION-shaped, not object-shaped); inventing one is out of scope for a name
            // list already computable server-side, so this ships the reshaped list instead — zero new Code
            // surface. Sorted for a stable, predictable palette order.
            var libNamesArr = new ExecArray
            {
                Items = (effective.Ui?.Functions ?? [])
                    .Where(fn => fn.Name is { Length: > 0 } && effective.SystemNames.Contains(fn.Name)
                        && ComponentReturnsElement(fn))
                    .Select(fn => fn.Name!)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .Select((n, i) => new ExecItem { Key = i, Value = new ExecText { Value = n } })
                    .ToList(),
                Id = --context.LastId.Value,
                Kind = ArrayKind.List,
            };

            return Obj(("db", seedDb), ("exprs", exprsObj), ("fns", fnsObj), ("types", typesObj), ("lib", libObj),
                ("libNames", libNamesArr), ("ambients", Obj()), ("params", Obj()));
        }
        catch (Exception ex)
        {
            // An invalid/unloadable design must not break the canvas — degrade db/exprs/fns/types to empty
            // (every expr chips) and log the FULL detail regardless of family. The structural canvas still
            // paints. `error` carries the already-formatted banner text (M12 eval-degrade-banner).
            // STILL ship lib + libNames: the standard library does not depend on this design's projection
            // (GenericUi.StdlibSource), and the component palette lists insert targets from libNames. A
            // bare-leaf render root (SchemaValidationException: root must be an element) is a common
            // authoring mid-state — without libNames the palette would be empty and "disabled insert"
            // would be untestable/unusable (no buttons to disable).
            Console.Error.WriteLine($"evalContext of design {design.Id} failed: {ex}");
            try
            {
                var effective = GenericUi.Effective(new InstanceDescription());
                var lib = new Dictionary<string, IExecValue>();
                foreach (var fn in effective.Ui?.Functions ?? [])
                {
                    if (fn.Name is not { Length: > 0 } || !effective.SystemNames.Contains(fn.Name)) continue;
                    var libJson = JsonSerializer.Serialize<ICodeValue>(fn, SchemaJson.Options);
                    lib[fn.Name] = Obj(("ast", new ExecText { Value = libJson }));
                }
                var libObj = new ExecObject { Id = --context.LastId.Value, Constant = true, Props = lib };
                var libNamesArr = new ExecArray
                {
                    Items = (effective.Ui?.Functions ?? [])
                        .Where(fn => fn.Name is { Length: > 0 } && effective.SystemNames.Contains(fn.Name)
                            && ComponentReturnsElement(fn))
                        .Select(fn => fn.Name!)
                        .OrderBy(n => n, StringComparer.Ordinal)
                        .Select((n, i) => new ExecItem { Key = i, Value = new ExecText { Value = n } })
                        .ToList(),
                    Id = --context.LastId.Value,
                    Kind = ArrayKind.List,
                };
                return Obj(("db", empty), ("exprs", empty), ("fns", empty), ("types", empty), ("lib", libObj),
                    ("libNames", libNamesArr), ("ambients", empty), ("params", empty),
                    ("error", new ExecText { Value = DegradeBannerText(ex) }));
            }
            catch
            {
                return Obj(("db", empty), ("exprs", empty), ("fns", empty), ("types", empty), ("lib", empty),
                    ("libNames", EmptyArr()), ("ambients", empty), ("params", empty),
                    ("error", new ExecText { Value = DegradeBannerText(ex) }));
            }
        }
    }

    // M12 eval-degrade-banner (ux review, item 3) — scope the VERBATIM exception message to the
    // DESIGNER-FACING family (SchemaValidationException + CodeParseException — the shapes an operator's
    // OWN authoring mistakes throw); any OTHER exception (a genuine bug / infra failure — e.g. the "No
    // design with id" CodeRuntimeException guard above) ships a GENERIC, calmer text instead — never leak
    // an unrelated stack-trace-adjacent detail to the canvas. The full exception is ALWAYS logged to
    // Console.Error regardless (see the catch above) — this only controls what reaches the client.
    // Extracted as its own method (public, like MountUrl/StripBase — the project's own precedent for a
    // directly-unit-testable static helper) so it is testable without needing a reachable non-validation
    // throw path through the whole builder.
    public static string DegradeBannerText(Exception ex) => ex is SchemaValidationException or CodeParseException
        ? $"Preview data unavailable: {ex.Message} — fix the design, then Refresh values."
        : "Preview data unavailable — see the server log.";

    // Seed the design's `initialData` into a THROWAWAY file-backed store (self-seeds when there is no data
    // file — JsonFileInstanceStore), read the graph back with DbBridge.LoadRoot (the SAME builder that binds
    // live `db`), and RE-MINT it with distinct negative ids + Constant so it can ship without colliding with
    // the designer's live data. The temp dir is deleted wholesale afterward.
    private static ExecObject LoadSeedGraph(InstanceDescription desc, ExecContext context)
    {
        var dir = Path.Combine(Path.GetTempPath(), "deenv-eval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new JsonFileInstanceStore(Path.Combine(dir, "data.json"), desc);
            var loaded = DbBridge.LoadRoot(store, desc, context);
            return (ExecObject)RemintConstant(loaded, context, new Dictionary<IExecValue, IExecValue>(ReferenceEqualityComparer.Instance));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    // Re-mint a loaded (positive-id) graph as a distinct-NEGATIVE-id, Constant graph — the PublishReportCode
    // idiom, applied to a whole object graph. Distinct negative ids: memo-result nodes register in the
    // client's GLOBAL object registry by id, so a positive id would clobber the designer's live data.
    // Constant: ClientState ships a Constant node WHOLE (every prop/item), so the seed graph arrives complete
    // for the client's own evaluation. Shared/cyclic structure is preserved via `seen` (keyed by original
    // reference) — a node reached twice re-mints once; the new object is registered BEFORE recursing so a
    // cycle terminates. Scalars/null are immutable → reused as-is. A set/dict item's Key tracks its re-minted
    // object id (DbBridge's Key==Value.Id invariant), keeping identity keys unique within the array.
    private static IExecValue RemintConstant(IExecValue value, ExecContext context, Dictionary<IExecValue, IExecValue> seen)
    {
        switch (value)
        {
            case ExecObject o:
            {
                if (seen.TryGetValue(o, out var existing)) return existing;
                var minted = new ExecObject
                {
                    Id = --context.LastId.Value, Constant = true, Props = new(),
                    TypeName = o.TypeName, SourcePath = o.SourcePath, ScalarEntry = o.ScalarEntry,
                };
                seen[o] = minted;
                foreach (var (k, pv) in o.Props) minted.Props[k] = RemintConstant(pv, context, seen);
                return minted;
            }
            case ExecArray a:
            {
                if (seen.TryGetValue(a, out var existing)) return existing;
                var minted = new ExecArray
                {
                    Id = --context.LastId.Value, Kind = a.Kind, Constant = true, Items = new(),
                    ElementTypeName = a.ElementTypeName, SourcePath = a.SourcePath,
                };
                seen[a] = minted;
                foreach (var item in a.Items)
                {
                    var mv = RemintConstant(item.Value, context, seen);
                    // Keep Key == Value.Id for set/dict (identity-keyed); a list keeps its ordinal key.
                    var key = a.Kind != ArrayKind.List && mv is ExecObject mo ? mo.Id : item.Key;
                    minted.Items.Add(new ExecItem { Key = key, Value = mv });
                }
                return minted;
            }
            default:
                return value; // scalars / null / nothing are immutable — reuse
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    // A static shell for an engine-level fallback page (an SSR error, or a NotFound when
    // even the synthesized NotFound view is unavailable). Normal pages use UiLayout.
    private static string Layout(string title, string body) => $"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>{Escape(title)}</title>
          <style>{Css}</style>
        </head>
        <body>
        {body}
        </body>
        </html>
        """;

    private const string Css = """
        body { font-family: system-ui, Arial, sans-serif; margin: 2rem; color: #222; }
        """;

    // The generic breadcrumb chrome. Its link TARGETS are the ROOT-RELATIVE cumulative node paths
    // (`/`, `/notes`, …), prefixed with the mount `base` at the edge so they navigate within the
    // mounted instance (the same MountUrl the attribute emitter applies). Its visible TEXT is the
    // LABELED trail: the root is the instance display name humanized (the app's identity, not the
    // internal root-type name "Db"); each segment is its `trail` label (a humanized prop name or a
    // member's labelProp value). `trail` is one label per URL segment, in order.
    // Breadcrumb hrefs are exempt from the AppendCodeAttribute scheme guard by construction: every
    // url here is built as "/" + path segments (root-relative), so it can never carry a scheme — not
    // a missed sink. (Client twin: ui.ts refreshBreadcrumbs.)
    private string Breadcrumbs(NodePath path, IReadOnlyList<string> trail, string @base)
    {
        var sb = new StringBuilder($"<nav class=\"breadcrumbs\"><a href=\"{Escape(MountUrl(@base, "/"))}\">{Escape(RootLabel())}</a>");
        var url = "";
        for (var i = 0; i < path.Segments.Count; i++)
        {
            url += "/" + path.Segments[i];
            var text = i < trail.Count ? trail[i] : path.Segments[i];
            sb.Append($" / <a href=\"{Escape(MountUrl(@base, url))}\">{Escape(text)}</a>");
        }
        sb.Append("</nav>");
        return sb.ToString();
    }

    // The <title> for a generic page: the labeled trail under the root label, e.g. "Devlog" at the
    // root, "Devlog / Milestones / Gate #3" deep. The same root label + labels the breadcrumb shows.
    private string TitleFromTrail(IReadOnlyList<string> trail) =>
        trail.Count == 0 ? RootLabel() : RootLabel() + " / " + string.Join(" / ", trail);

    // The breadcrumb/title ROOT label: the instance display name, humanized (e.g. "devlog" → "Devlog").
    // Falls back to "Db" (the historical root-type label) when no name was threaded in (a bare/unit
    // render). The client mirrors this from window.initAppName (ui.ts rootLabel).
    private string RootLabel() => _appName.Length > 0 ? TextUtil.Humanize(_appName) : "Db";

    private static NodePath ParsePath(string urlPath)
    {
        var segs = urlPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return NodePath.FromSegments(segs);
    }

    // ── mount base (the front-edge addressing seam) ─────────────────────────────
    //
    // The instance is mount-UNAWARE: its `path` var and the URLs its Code emits are root-relative, so
    // the SAME instance serves at a path (`/apps/todo`) or a domain root unchanged. The `base` is
    // applied ONLY here, at the edges (SSR link/breadcrumb/script emission), and mirrored on the
    // client (ui.ts/init.ts). These two helpers are the whole seam in C#.

    // Navigational URL attribute names — the ones whose root-relative value the mount base prefixes,
    // and the ones the dangerous-scheme guard applies to.
    private static bool IsUrlAttribute(string name) => name is "href" or "src";

    // An inline event-handler attribute name (onclick, onmouseover, onload, …). Only ever legitimate
    // as a `fn` value (wired client-side; a fn never reaches AppendCodeAttribute's scalar switch) — a
    // SCALAR value for one of these names is dropped by the caller regardless of type. `on` alone
    // (length 2) is not an event name, so the length floor avoids over-matching a coincidental "on".
    private static bool IsEventAttribute(string name) =>
        name.Length >= 3 && name.StartsWith("on", StringComparison.OrdinalIgnoreCase);

    // A URL whose scheme is one an attacker can use to run script from a clicked/loaded link
    // (`javascript:`, `data:` — e.g. `data:text/html,<script>…`, `vbscript:`). Browsers strip
    // embedded TAB/CR/LF anywhere in a URL before scheme-sniffing (the classic "java\tscript:" /
    // "java\nscript:" bypass), and ignore leading ASCII whitespace/control characters, so both are
    // stripped/trimmed here before the case-insensitive scheme match — matching what a browser would
    // actually resolve the URL to, not just the literal authored text.
    private static readonly string[] DangerousSchemes = ["javascript:", "data:", "vbscript:"];

    private static bool HasDangerousScheme(string url)
    {
        var stripped = url.Replace("\t", "").Replace("\r", "").Replace("\n", "");
        var start = 0;
        while (start < stripped.Length && stripped[start] <= ' ') start++;
        var trimmed = stripped[start..];
        foreach (var scheme in DangerousSchemes)
            if (trimmed.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // Prefix a ROOT-RELATIVE url with the mount base. Identity when base is "/" (root-mounted) or the
    // url is not root-relative (an absolute "http(s)://…", a protocol-relative "//host", a fragment
    // "#", or a relative "foo" — none are mount-rebased). `base` "/apps/todo" + url "/notes/2" →
    // "/apps/todo/notes/2"; + "/" → "/apps/todo".
    public static string MountUrl(string @base, string url)
    {
        if (@base == "/" || @base.Length == 0) return url;
        if (!url.StartsWith('/') || url.StartsWith("//")) return url; // not root-relative → leave it
        var trimmed = @base.TrimEnd('/');
        return url == "/" ? trimmed : trimmed + url;
    }

    // Strip the mount base off a FULL request path to recover the instance's root-relative path (the
    // inverse of MountUrl; the client init.ts mirrors it for the `path` var). "/apps/todo/notes/2"
    // with base "/apps/todo" → "/notes/2"; "/apps/todo" → "/". Identity when base is "/". A path that
    // does not start with the base is returned unchanged (defensive — the router only routes matches).
    public static string StripBase(string @base, string fullPath)
    {
        if (@base == "/" || @base.Length == 0) return fullPath;
        var trimmed = @base.TrimEnd('/');
        if (fullPath == trimmed) return "/";
        if (fullPath.StartsWith(trimmed + "/", StringComparison.Ordinal))
        {
            var rest = fullPath[trimmed.Length..];
            return rest.Length == 0 ? "/" : rest;
        }
        return fullPath;
    }

    private static string Escape(string s) =>
        System.Net.WebUtility.HtmlEncode(s);
}
