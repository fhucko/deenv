using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeEnv.Code;
using DeEnv.Instance;
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

    public SsrRenderer(IInstanceStore store, InstanceDescription desc, ClientSessionStore? sessions = null,
        LiveRegistry? registry = null, string appName = "")
    {
        _store = store;
        _desc = desc;
        _resolver = new TypeResolver(desc);
        _sessions = sessions;
        _registry = registry ?? new LiveRegistry();
        _appName = appName;
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
            var (result, title, scope, status, trail) = ExecuteRender(urlPath, context, principalUserId: principalUserId);
            var body = new StringBuilder();
            SerializeChild(result, body, @base);

            // Mint a session and ship its clientId, so the WS can claim it (hello) and a
            // later milestone can hang per-client push on it. The id is all the client
            // needs; a refetch re-renders over a fresh store load. See ClientSession.
            var clientId = _sessions?.Create().Id ?? "";

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
                    clientId, @base, assetAuthority, _appName),
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
        var context = new ExecContext { Seed = seed };
        context.LastId.Value = Math.Min(0, lastIdFloor);
        var (_, _, scope, _, _) = ExecuteRender(urlPath, context, sessionVars, warmDb, principalUserId, harvestAction);
        return ClientState.Serialize(scope, context);
    }

    // `Trail` is the labeled breadcrumb segments — one human-readable label per URL path segment
    // (a humanized prop name, or a member's labelProp value), in order. Empty for a custom render
    // (which owns its own chrome) or the root page. `Title` already joins them under the root label.
    private (IExecTagChild Result, string Title, ExecScope Scope, int Status, IReadOnlyList<string> Trail) ExecuteRender(
        string urlPath, ExecContext context, IReadOnlyDictionary<string, IExecValue>? sessionVars = null,
        ExecObject? warmDb = null, int? principalUserId = null, string? harvestAction = null)
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

        var exec = new CodeExecutor(_store, _descriptors, _resolver, floor);
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
                        AccessFloor.ScalarObject(UserConvention.TypeName, 0, new ObjectValue(new Dictionary<string, NodeValue>()))),
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
        string @base, string assetAuthority, string appName) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>{{Escape(title)}}</title>
          <style>{{ViewChromeCss}}</style>
          <script>window.initData={{initData}};window.initUi={{initUi}};window.initClientId="{{clientId}}";window.initBase="{{JsStringSafe(@base)}}";window.initAssetAuthority="{{JsStringSafe(assetAuthority)}}";window.initAppName="{{JsStringSafe(appName)}}";</script>
          <script>(function(){var a=window.initAssetAuthority,b=window.initBase==="/"?"":window.initBase;var s=document.createElement("script");s.src=a?location.protocol+"//"+a+b+"/js":b+"/js";document.head.appendChild(s);})();</script>
        </head>
        <body>{{breadcrumbs}}<div id="app">{{body}}</div></body>
        </html>
        """;

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
        .set-add, .dict-add, .ref-create, .add-type, .add-prop, .create-instance, .rename-save, .apply-design,
        .add-user, .add-list-btn, .add-item-btn {
          background: var(--green); border-color: var(--green); color: #fff; }
        .set-add:hover, .dict-add:hover, .ref-create:hover, .add-type:hover, .add-prop:hover,
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

        .ref-new { display: flex; flex-wrap: wrap; gap: 0.5rem; align-items: end;
          margin-top: 0.4rem; padding: 0.8rem; background: var(--bg); border: 1px dashed var(--border); border-radius: 8px; }
        .ref-new input, .ref-new select { max-width: 200px; }
        .dict-error { color: var(--danger); font-size: 0.85rem; margin-top: 0.4rem; }
        .set-empty, .dict-empty { color: var(--muted); font-size: 0.9rem; margin: 0.1rem 0 0; }

        /* Flag-gated create view: the `+ New` button replaces the old always-visible inline add row;
           clicking it reveals a labeled create form (.create-form, reusing the .object-form card +
           .field labels) BELOW the still-visible read-only table, with Save (primary green,
           .set-add/.dict-add) and a plain Cancel — the list stays in view while appending. Hidden
           until asked — the create-then-populate model; collections are added on the entry's own page
           after it exists. (The table's own bottom margin separates it from the create-form card.) */
        .new-btn { margin-top: 0.3rem; border-color: var(--accent); color: var(--accent); }
        .new-btn:hover { background: color-mix(in srgb, var(--accent) 8%, var(--surface)); border-color: var(--accent); }
        .create-form > .field > input:not([type=checkbox]), .create-form > .field > select { width: 100%; max-width: 440px; }
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
        .set-password { display: flex; align-items: center; gap: 0.5rem;
          margin: 0.9rem 0 0; padding-top: 0.9rem; border-top: 1px solid var(--border); }
        .set-password label.new-password { color: var(--muted); }
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
    private static void AppendCodeAttribute(StringBuilder sb, string name, IExecValue value, string @base)
    {
        switch (value)
        {
            case ExecText t:
                var text = IsUrlAttribute(name) ? MountUrl(@base, t.Value) : t.Value;
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
        AccessFloor.LoadPrincipal(_store, principalUserId);

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

    // Navigational URL attribute names — the ones whose root-relative value the mount base prefixes.
    private static bool IsUrlAttribute(string name) => name is "href" or "src";

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
