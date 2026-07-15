Feature: Designer - Components and Live Previews


  # ...and MetaUse configuration rows (`f.uses.orderBy(u => u.order)` — already sorted for display, unlike
  # fns/vars), reusing the exact same helper on a different set.
  @m12 @single-user
  Scenario: A component's configurations can be reordered with the same move controls
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "scratchcomp"
    And I edit the design "scratchcomp"
    And I expand the Advanced code disclosure
    And I author a bare convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I click the add-component button
    And I click the add-configuration button
    And I set configuration 0's name to "aa"
    And I click the add-configuration button
    And I set configuration 1's name to "bb"
    Then configurations read, in order: "aa, bb"
    When I click move-down on configuration 0
    Then configurations read, in order: "bb, aa"


  # Unwrap is disabled — with an honest reason surfaced as its title — for: a root with MORE than one
  # child (no unambiguous replacement), an empty element (nothing to splice — remove instead), and a
  # component-call row (its children are a render-prop body, not structural content).
  @m12 @single-user
  Scenario: Unwrap is disabled for a multi-child root, an empty element, and a component-call row, with an honest reason
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "unwrapme"
    And I edit the design "unwrapme"
    And I expand the Advanced code disclosure
    And I author an unwrap-test convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I add a type to the design
    And I name the just-added type "Db"
    And I add a field "greeting" to the type "Db"
    Then the root node's unwrap button is disabled
    And the root node's unwrap button's title reads "the root can only unwrap with exactly one element child"
    When I add a child element to the root node
    Then the root node's last child's unwrap button is disabled
    And the root node's last child's unwrap button's title reads "no children to splice — remove this element instead"
    When I edit the root node's last child tag input to "ConfirmButton"
    And I click Refresh values
    Then the root node's last child's unwrap button is disabled
    And the root node's last child's unwrap button's title reads "a component call's children are its body — unwrap doesn't apply"


  # ──── M12 F1 — structured fns: rows + import + projection + editor ────────────────────────────────────────────────────────────────
  #
  # S1a/S1b/E1/E2 gave the render TREE structured storage + an editor; F1 does the same for named
  # FUNCTIONS — a design's `ui` can carry a scalar HELPER (a single-return expression, e.g. a ternary)
  # and a COMPONENT (a single-return element with a param) besides `fn render()`. Import now lifts the
  # old helper-function refusal (SchemaBridgeTests / DesignerSourceTests cover the server-only /
  # lambda-return / multi-statement refusals and the round-trip at the unit level); this scenario
  # proves the DESIGNER-FACING half: the imported functions show up as an editable "Components" area
  # (name input, comma-separated params input, its own body tree via the SAME recursive
  # renderNodeEditor), and editing a component's params field persists — an ordinary two-way-bound
  # ctx field write, exactly like editing a node's tag — and the projected document carries the edit.
  @m12 @single-user
  Scenario: The Components area shows an imported component function and editing its params persists into the projection
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "compme"
    And I edit the design "compme"
    And I expand the Advanced code disclosure
    And I author a convertible render with a component function into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    And the Components area shows a component named "NoteCard" with params "note"
    # A projectable app document needs a Db root type (the render/component leaves reference `db`/`note`
    # fields, but projection only PARSES those expression sources — it does not need them to resolve — a
    # Db type is still required for the document as a whole to load).
    When I add a type to the design
    And I name the just-added type "Db"
    And I add a field "greeting" to the type "Db"
    And I edit the component "NoteCard"'s params to "note, extra"
    Then the stored component "NoteCard" has params "note, extra"
    And the stored render for "compme" projects to a valid design document


  # ──── M12 V1 — MetaVar rows: component state + top-level ui vars ──────────────────────────────────────────────────────────────────────
  #
  # F1 imported stateless helpers/components; V1 lifts the LAST two import refusals: top-level `ui var`s
  # AND a real stateful setup/view component (`var state`, a nested `fn render()`, `return render` — the
  # canonical shape confirmed against the designer's own designEditor and every stateful GenericUi library
  # component). The imported state var shows in the component's own card (name + init inputs, the SAME
  # idiom the render tree's leaf/attr editing already uses); editing its init persists into the MetaVar row
  # and the projected document. A design-level "State" area (Design.vars) offers the same add/remove idiom
  # for top-level state.
  #
  # fnVarNameHint's "'render' is reserved" check (app.deenv has NO comment syntax, so the rationale lives
  # here): load-bearing, NOT the same "reserved name" story as the top-level fnNameHint above — a STATEFUL
  # fn's projection (SchemaBridge.ProjectRenderUi) SYNTHESIZES a nested `fn render()` inside the
  # component's own body, so a state var named "render" collides with that synthesized function in the
  # SAME scope and would be silently overwritten. The server refuses this too
  # (DesignerSourceTests.ProjectDesignDocument_refuses_a_fn_level_state_variable_named_render). Its
  # "shadows a parameter" check (sys.hasParam) is the SAME silent-last-wins clobber class but hinted, not
  # refused, on purpose: a directly-authored clobber the operator chose (params/vars share one function
  # scope, whichever binds last wins), not one projection introduces — arch's call, no new permanent
  # restriction.
  @m12 @single-user
  Scenario: A stateful component's state var shows in its card, editing its init persists, and a design-level State var can be added
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "counterme"
    And I edit the design "counterme"
    And I expand the Advanced code disclosure
    And I author a convertible render with a stateful Counter component into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    And the Components area shows a component named "Counter" with a state var named "count" and init "0"
    When I edit component "Counter"'s state var "count" init to "1"
    Then the stored state var "count" has init "1"
    When I click the add-design-state-var button
    Then the design's State area shows 1 state var row
    And design-level state var 0 shows the "name required" hint
    When I click the add-design-state-var button
    Then the design's State area shows 2 state var rows
    When I set design-level state var 0's name to "dup"
    And I set design-level state var 1's name to "dup"
    Then design-level state var 1 shows the "duplicate name" hint
    When I remove the last design-level state var
    And I remove the last design-level state var
    Then the design's State area shows 0 state var rows


  # ──── M12 F1 review fix (ui-arch + ux) — the from-scratch "+ Component" flow, no import ────────────────────────
  #
  # F1's OWN browser test above only exercised a component that arrived via IMPORT (already has a body
  # root). This proves the OTHER path: "+ Component" mints a MetaFn with an EMPTY body (the reviewed,
  # upheld decision — a true atomic two-object mint isn't reachable from a plain click handler), so the
  # new card's body shows a ROOT-position add-row. That row must offer ONLY "+ element"/"+ text/expr" —
  # NOT "+ for"/"+ if" (a for/if row can never be a fn's body root — projection refuses it — and a body
  # root has no remove ×, so a for/if click would strand the operator until they delete the WHOLE
  # component). Also proves the inline "'render' is reserved" name hint (fix 3) and that removing the
  # component (its OWN × in the fn-head) cleanly removes the card.
  @m12 @single-user
  Scenario: A from-scratch component starts with a root-only add-row, gains its first body node, shows the reserved-name hint, and removes cleanly
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "scratchcomp"
    And I edit the design "scratchcomp"
    And I expand the Advanced code disclosure
    And I author a bare convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I click the add-component button
    Then a new component card appears with an empty body
    And the new component's body add-row offers only element and text, not for or if
    When I add an element to the new component's body
    Then the new component's body shows an element node
    When I set the new component's name to "render"
    Then the new component shows the reserved-name hint
    When I remove the new component
    Then the new component card is gone


  # ──── M12 F2 — canvas expansion of design-component invocations ────────────────────────────────────────────────────────────────
  #
  # F1 gave a design's `fn NoteCard(note)` a first-class Components row; F2 makes the canvas EXPAND an
  # invocation of it (`<NoteCard note={n}/>`) into the component's OWN rendered content — real <li> text,
  # not a literal <NoteCard> element and not a chip — the runtime-faithful canvas resolution S4 selection
  # will build on. THE LIVENESS PROOF: editing the component's body leaf repaints every expansion SAME-FRAME
  # (no Refresh) — proving expansion runs through the SAME live row-data dep-recording the element/for/if
  # walks already prove, not a cached/refreshed snapshot.
  @m12 @single-user
  Scenario: The canvas expands a component invocation into its real content, and editing the component body repaints every expansion live
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "expandme"
    And I edit the design "expandme"
    When I add a type to the design
    And I name the just-added type "Db"
    When I add a type to the design
    And I name the just-added type "Note"
    And I add a field "title" to the type "Note"
    When I add a field "notes" to the type "Db"
    When I reload the design editor
    And I retype the prop "notes" to "Note"
    And I set the prop "notes" cardinality to "set"
    When I ensure the Advanced code disclosure is open
    And I set the design's initial data to:
      """
      initialData
          Db 1
              notes: [2, 3]
          Note 2
              title: "Alpha"
          Note 3
              title: "Beta"
      """
    When I ensure the Advanced code disclosure is open
    And I author a component-invoking convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    And the design canvas shows a "li" element reading "Alpha"
    And the design canvas shows a "li" element reading "Beta"
    When I edit the component "NoteCard"'s body leaf to "\"Changed\""
    Then the design canvas shows a "li" element reading "Changed"


  # ──── M12 F3b review fix — an UNNAMED fn (the "+ Component" mid-authoring state) is symmetrically
  # excluded from the staleness comparison ──────────────────────────────────────────────────────────────────────────────────────────────────────────
  #
  # F1's "+ Component" mints a MetaFn with `name:""` — the NORMAL mid-authoring state, not an error.
  # An unnamed fn has no call sites, so it cannot make any call result stale: the staleness
  # comparison (FnsStale/fnsStale, both twins) skips empty-named rows symmetrically with the fact
  # that ctx.fns can never ship one either (an unnamed fn also blocks projection entirely, per F1's
  # own refusal) — so the freshly-minted unnamed component shows NO banner. Naming it makes it a
  # real callable the STALE ctx doesn't know about yet — the banner correctly appears — and Refresh
  # (which rebuilds ctx over the now-valid, now-named, now-bodied fn) clears it.
  #
  # The root type carries a field (a VALID design, unlike E2-era fixtures) so evalContext SUCCEEDS —
  # a fieldless root would now degrade ctx and, per the eval-degrade-banner suppression fix, the
  # degrade banner would subsume this scenario's OWN staleness banner it means to prove.
  @m12 @single-user
  Scenario: A freshly-minted unnamed component shows no staleness banner; naming it shows the banner correctly, and Refresh clears it
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "scratchcomp"
    And I edit the design "scratchcomp"
    When I add a type to the design
    And I name the just-added type "Db"
    And I add a field "note" to the type "Db"
    When I ensure the Advanced code disclosure is open
    And I author a bare convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I click the add-component button
    Then a new component card appears with an empty body
    And the design canvas does not show the stale-fns banner
    When I add an element to the new component's body
    And I set the new component's name to "Foo"
    Then the design canvas shows the stale-fns banner
    When I click Refresh values
    Then the design canvas does not show the stale-fns banner


  # ──── M12 V1b — init-evaluated state in the static canvas ──────────────────────────────────────────────────────────────────────────────
  #
  # A static canvas can only ever show INITIAL state, so binding each state var's init expression IS
  # the truth — what a fresh live instance shows at mount. V1 left a stateful component's state-var
  # references chipped ("honestly unbound until W1's live instances"); V1b flips that: BindVars/
  # bindVars now binds a var's init value at the walk ROOT (design.vars, top-level `ui var`s) and at
  # ExpandFn/expandFn (a MetaFn's OWN vars, bound AFTER its params) — real content, not a placeholder
  # chip. The proof imports a design-level var `greeting` (referenced in its own <span>) AND a real
  # stateful Counter() component INVOKED in the render (F2's tag-expansion) — one fixture exercising
  # both binding sites. THE RACE GUARD (the same S3a/CANVAS-EVAL-1 idiom, now for a var's init):
  # editing the var's init text is a live row read (dep-recorded — same-frame), but the NEW init text
  # has no `ctx.exprs` entry yet (a refresh-gated snapshot), so the var is left UNBOUND until Refresh —
  # the referencing leaf (whose OWN source text never changed) falls to an honest chip holding its raw
  # source "greeting", never the edited init text. Clicking "Refresh values" rebuilds ctx (which
  # re-collects every var init source fresh, RenderExprSources' collector-law obligation) and the leaf
  # shows the new value.
  @m12 @single-user
  Scenario: The canvas shows a stateful component's INITIAL state and a design-level var's init value, chipping an edited init until Refresh
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "initstate"
    And I edit the design "initstate"
    When I add a type to the design
    And I name the just-added type "Db"
    And I add a field "note" to the type "Db"
    When I ensure the Advanced code disclosure is open
    And I author a convertible render with a design var and an invoked Counter component into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    And the design canvas shows a "button" element reading "0"
    And the design canvas shows a "span" element reading "hi"
    When I edit design-level state var 0's init to "\"bye\""
    Then the design canvas shows an expression chip reading "greeting"
    When I click Refresh values
    Then the design canvas shows a "span" element reading "bye"


  # ──── M12 U1 — MetaUse rows: the Configurations editor + static per-configuration preview ──────────
  #
  # F1 gave a component its own Components card; U1 adds a Configurations area under it — each row a
  # stored MetaUse (name + args, the SAME MetaAttr shape an invocation's own attrs already have,
  # extracted into the shared `attrRow` fn the tree editor's own attrs listing now also calls) rendering
  # a STATIC per-configuration preview: the designer synthesizes a TRANSIENT invocation node
  # (`{ kind: "", tag: fn.name, expr: "", attrs: use.args, children: [] }` — no `order`, never a real
  # MetaNode row) and feeds it to the EXISTING F2 `sys.renderTree` expansion, so the preview shows the
  # component's REAL rendered content with the configuration's args bound — the same mechanism the main
  # canvas already proves, reused rather than reimplemented. `children: []` is REQUIRED, not defensive:
  # whenever the tag does NOT resolve against `fns` (a typo, or a design var shadowing the component's
  # own name — F2 grill E1), the walk falls to the literal-ELEMENT arm, which reads `children` through
  # the non-optional reader and throws on an absent field (conformance-pinned). The arg value is
  # deliberately DB-ROOTED (non-literal), exercising the F2 EvaluateCtxExpr binding path an ordinary
  # invocation's attrs already take (not just the LiteralValue tier-0 case). A typo'd arg name (matching
  # no declared param) shows an inline hint and clears once corrected — a typo is otherwise byte-identical
  # to no arg at all (both silently bind null). Two configurations bound to DIFFERENT db-rooted values
  # render DIFFERENT content in their OWN panels (the independence-at-static-level pin — scoped per-row,
  # not "this text appears somewhere"); removing one configuration removes its whole row.
  @m12 @single-user
  Scenario: A component's Configurations area previews each stored use with its own bound args, independently, and removing one clears its row
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "compme"
    And I edit the design "compme"
    And I add a type to the design
    And I name the just-added type "Db"
    And I add a type to the design
    And I name the just-added type "Note"
    And I add a field "title" to the type "Note"
    When I add a field "noteA" to the type "Db"
    And I add a field "noteB" to the type "Db"
    When I reload the design editor
    And I retype the prop "noteA" to "Note"
    And I retype the prop "noteB" to "Note"
    When I ensure the Advanced code disclosure is open
    And I set the design's initial data to:
      """
      initialData
          Db 1
              noteA: 2
              noteB: 3
          Note 2
              title: "Alpha"
          Note 3
              title: "Beta"
      """
    When I ensure the Advanced code disclosure is open
    And I author a convertible render with a component function into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    And the Components area shows a component named "NoteCard" with params "note"
    When I click the add-configuration button
    Then component configurations shows 1 row
    And configuration 0 shows the "name required" hint
    When I set configuration 0's name to "empty"
    And I add an arg to configuration 0
    And I set configuration 0's arg 0 name to "nope"
    Then configuration 0's arg 0 shows the "no such param" hint
    When I set configuration 0's arg 0 name to "note"
    Then configuration 0's arg 0 shows no hint
    When I set configuration 0's arg 0 value to "db.noteA"
    Then configuration 0's preview shows a "li" element reading "Alpha"
    When I click the add-configuration button
    Then component configurations shows 2 rows
    When I set configuration 1's name to "long list"
    And I add an arg to configuration 1
    And I set configuration 1's arg 0 name to "note"
    And I set configuration 1's arg 0 value to "db.noteB"
    Then configuration 1's preview shows a "li" element reading "Beta"
    And configuration 0's preview shows a "li" element reading "Alpha"
    When I remove configuration 1
    Then component configurations shows 1 row


  # ──── M12 W1a — the live-instance driver ────────────────────────────────────────────────────────────────────────────────────────────────────────────────
  #
  # U1 gave a configuration a STATIC preview (the row-walk simulator, sys.renderTree). W1a replaces it
  # with a REAL running instance of the previewed component — the SAME client runtime, sandboxed (its own
  # deep-copied seed graph, its own private memo cache, wsHooks nulled) — "preview = live", not a second
  # engine. Distinguished from the static walk by the walk's OWN provenance marker ("data-node", stamped
  # on every element the row-walk emits — never emitted by the real runtime), so "a live element with this
  # text" is an unambiguous, structural proof, not an inference from behavior alone. The opaque-container
  # pin: marking the mounted node and forcing an UNRELATED page re-render (editing the design's own label)
  # proves the mount hook is idempotent — it never rebuilds an unchanged instance's DOM.
  @m12 @single-user
  Scenario: A stateful component's configuration mounts a real live instance, and an unrelated page re-render never clobbers it
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "counterme"
    And I edit the design "counterme"
    And I add a type to the design
    And I name the just-added type "Db"
    And I add a field "note" to the type "Db"
    When I ensure the Advanced code disclosure is open
    And I author a convertible render with a stateful Counter component into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I click the add-configuration button
    Then component configurations shows 1 row
    Then configuration 0's live instance shows a "button" element reading "0"
    When I mark configuration 0's live instance node
    And I rename the design's label to "counterme-renamed"
    Then configuration 0's live instance node is unchanged since marking


  # Independence-at-mount (two configurations, two separate sandboxes, two separate answers) AND the
  # page-side args-signature remount (editing a use's arg text re-mounts exactly that instance, over its
  # OWN fresh sandbox — the other configuration is untouched throughout).
  @m12 @single-user
  Scenario: Two configurations with different args mount independent live instances, and editing an arg remounts its own instance
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "compme"
    And I edit the design "compme"
    And I add a type to the design
    And I name the just-added type "Db"
    And I add a type to the design
    And I name the just-added type "Note"
    And I add a field "title" to the type "Note"
    When I add a field "noteA" to the type "Db"
    And I add a field "noteB" to the type "Db"
    When I reload the design editor
    And I retype the prop "noteA" to "Note"
    And I retype the prop "noteB" to "Note"
    When I ensure the Advanced code disclosure is open
    And I set the design's initial data to:
      """
      initialData
          Db 1
              noteA: 2
              noteB: 3
          Note 2
              title: "Alpha"
          Note 3
              title: "Beta"
      """
    When I ensure the Advanced code disclosure is open
    And I author a convertible render with a component function into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I click the add-configuration button
    And I set configuration 0's name to "a"
    And I add an arg to configuration 0
    And I set configuration 0's arg 0 name to "note"
    And I set configuration 0's arg 0 value to "db.noteA"
    Then configuration 0's live instance shows a "li" element reading "Alpha"
    When I click the add-configuration button
    And I set configuration 1's name to "b"
    And I add an arg to configuration 1
    And I set configuration 1's arg 0 name to "note"
    And I set configuration 1's arg 0 value to "db.noteB"
    Then configuration 1's live instance shows a "li" element reading "Beta"
    And configuration 0's live instance shows a "li" element reading "Alpha"
    When I set configuration 0's arg 0 value to "db.noteB"
    Then configuration 0's live instance shows a "li" element reading "Beta"


  # The v1 fidelity boundary, made honest not silent: a component reading an AMBIENT (currentUser — no
  # per-use ambients yet, unlike schema/extent which W1c now SEEDS — see the W1c section below) ALWAYS
  # misses against the workbench's sandbox scope — the driver shows the real interpreter error rather than
  # a blank card, and the page keeps working (a second configuration can still be added).
  @m12 @single-user @m12-live-isolation
  # NOTE (per 2026-07-15 analysis + grill): This scenario (and the throwing sibling error in DesignerLibrary)
  # intentionally proves cross-card + page isolation *within a single kernel boot + single page render*.
  # The error on first config + successful add of second config is the proof. Do not split; use filters.
  Scenario: A component reading an unseeded ambient shows the real error in its configuration card, and the page stays alive
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "brokencomp"
    And I edit the design "brokencomp"
    And I add a type to the design
    And I name the just-added type "Db"
    And I add a field "note" to the type "Db"
    When I ensure the Advanced code disclosure is open
    And I author a convertible render with an ambient-reading component into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I click the add-configuration button
    Then configuration 0's live instance shows the error "Variable currentUser not found"
    When I click the add-configuration button
    Then component configurations shows 2 rows


  # ──── M12 W1c — sandbox cache seeding: schema:/extent:/canWrite:/canRead: + library binding ──────────────
  #
  # W1a/W1b always missed a store-backed builtin (sys.schema/sys.new/sys.extent/sys.canWrite/sys.canRead)
  # against the workbench's fresh, unseeded private cache — the v1 fidelity boundary, ledgered as a
  # fast-follow (component-workbench.md). W1c seeds it FROM THE DESIGN'S OWN ROWS: `sys.evalContext`'s
  # payload (SsrRenderer.BuildEvalContext) now ships every declared type's descriptor (`types`, the SAME
  # shape a live page's `schema:*` cache holds) and the standard library's own function ASTs (`lib`,
  # bound into the sandbox scope alongside the design's own `fns`) — workbench.ts seeds the instance's
  # private cache from `types` at mount/Reset/every render pass (extent: re-derived every pass from the
  # instance's OWN db copy, so a handler's write stays visible — see seedExtentCache's own comment).
  # canWrite/canRead ship unconditionally true (no access floor to evaluate in a sandbox previewing the
  # operator's own design). Ambients (currentUser/path) remain the ONE still-real fidelity gap (the two
  # scenarios above this section).
  @m12 @single-user
  Scenario: A component composing Field over sys.schema renders the real field editor in its configuration card
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "seedschema"
    And I edit the design "seedschema"
    And I add a type to the design
    And I name the just-added type "Db"
    And I add a field "note" to the type "Db"
    And I add a type to the design
    And I name the just-added type "Note"
    And I add a field "title" to the type "Note"
    When I ensure the Advanced code disclosure is open
    And I author a convertible render with a schema-backed Field component into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I click the add-configuration button
    Then configuration 0's live instance shows a "label" element reading "Title"


  # sys.extent("Note") over the seed data — the seeded db copy's OWN "notes" set IS the extent (the design
  # doc's chosen per-instance derivation), so the card lists exactly the two seeded rows.
  @m12 @single-user
  Scenario: A component using sys.extent over the seed data lists the seeded rows in its configuration card
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "seedextent"
    And I edit the design "seedextent"
    And I add a type to the design
    And I name the just-added type "Db"
    And I add a type to the design
    And I name the just-added type "Note"
    And I add a field "title" to the type "Note"
    When I add a field "notes" to the type "Db"
    When I reload the design editor
    And I retype the prop "notes" to "Note"
    And I set the prop "notes" cardinality to "set"
    When I ensure the Advanced code disclosure is open
    And I set the design's initial data to:
      """
      initialData
          Db 1
              notes: [2, 3]
          Note 2
              title: "Alpha"
          Note 3
              title: "Beta"
      """
    When I ensure the Advanced code disclosure is open
    And I author a convertible render with an extent-listing component into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I click the add-configuration button
    Then configuration 0's live instance shows a "li" element reading "Alpha"
    And configuration 0's live instance shows a "li" element reading "Beta"


  # A LIBRARY component (RefSelect, one of the sys.schema-dependent components the v1 boundary excluded —
  # "lib components render as empty literal elements") composing sys.extent for its own candidates — bound
  # into the sandbox scope alongside the design's own fns (ctx.lib, BuildEvalContext), never the page's own
  # scope (the design's rejected-parenting guard stays intact). Renders its REAL <select>/<option> UI.
  @m12 @single-user
  Scenario: A library component composing sys.extent renders its real UI in the configuration card
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "seedlib"
    And I edit the design "seedlib"
    And I add a type to the design
    And I name the just-added type "Db"
    And I add a type to the design
    And I name the just-added type "Note"
    And I add a field "title" to the type "Note"
    When I add a field "notes" to the type "Db"
    And I add a field "pick" to the type "Db"
    When I reload the design editor
    And I retype the prop "notes" to "Note"
    And I set the prop "notes" cardinality to "set"
    And I retype the prop "pick" to "Note"
    When I ensure the Advanced code disclosure is open
    And I set the design's initial data to:
      """
      initialData
          Db 1
              notes: [2, 3]
          Note 2
              title: "Alpha"
          Note 3
              title: "Beta"
      """
    When I ensure the Advanced code disclosure is open
    And I author a convertible render with a RefSelect component into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I click the add-configuration button
    Then configuration 0's live instance shows a "option" element reading "Alpha"
    And configuration 0's live instance shows a "option" element reading "Beta"


  # CALLEE-ONLY — clicking inside an EXPANDED component invocation selects the COMPONENT's own body row
  # (its .fn-body, not the caller's row in the main render tree), matching F2's provenance decision: every
  # expanded element carries its own body-row data-node, never the invocation row's.
  @m12 @single-user
  Scenario: Clicking inside an expanded component's content in the canvas selects the component's own body row, not the caller's
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "expandme"
    And I edit the design "expandme"
    When I add a type to the design
    And I name the just-added type "Db"
    When I add a type to the design
    And I name the just-added type "Note"
    And I add a field "title" to the type "Note"
    When I add a field "notes" to the type "Db"
    When I reload the design editor
    And I retype the prop "notes" to "Note"
    And I set the prop "notes" cardinality to "set"
    When I ensure the Advanced code disclosure is open
    And I set the design's initial data to:
      """
      initialData
          Db 1
              notes: [2, 3]
          Note 2
              title: "Alpha"
          Note 3
              title: "Beta"
      """
    When I ensure the Advanced code disclosure is open
    And I author a component-invoking convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    And the design canvas shows a "li" element reading "Alpha"
    When I click the design canvas "li" element reading "Alpha"
    Then the component "NoteCard"'s body row is selected
    And no tree editor row is selected in the main render tree


  # ──── M12 S5b — the palette + insert-at-selection ────────────────────────────────────────────────────────────────────────────────────────────────────
  #
  # "The library IS the palette" (visual-designer.md foreclosure guard): no registry, nothing needs
  # registration to appear. This design's own component (Badge, a design.fns row) and standard-library
  # components (shipped honestly via ctx.libNames — S5b's seam answer: the language has no dict/keys()
  # enumeration, so BuildEvalContext reshapes ctx.lib's own keys into a plain array) both list, purely
  # because they are in scope. The Library group is further filtered by AST SHAPE
  # (ComponentReturnsElement, SsrRenderer.cs — review fold #4, then WIDENED once the first cut's "bare
  # single return" predicate excluded the library's own flagship components): a lib fn appears only when
  # EVERY return path of its own body (or, for the stateful setup/view idiom, its nested `fn render()`'s
  # body) provably yields an element — reflection over the code itself, not a registry; local vars and
  # helper fn decls are ignored, every if/else-if branch is walked. A name whose own return is not
  # provably a literal element — a bare symbol (the library's own top router: `return view`) or a call
  # expression (route()'s `return NotFoundForm()` — tracing INTO it is exactly the interprocedural
  # reasoning this per-fn rule does not do) or a scalar (InputType/boolGlyph) — honestly drops out. A
  # minimal Db type is added first: `sys.evalContext` (ctx.libNames' source) degrades to an empty payload
  # for a typeless design (InstanceDescriptionLoader requires a root Db type), so the Library group needs
  # one.
  @m12 @single-user
  Scenario: Opening the palette lists both the design's own components and the library
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "palettelist"
    And I edit the design "palettelist"
    When I add a type to the design
    And I name the just-added type "Db"
    When I add a field "note" to the type "Db"
    When I ensure the Advanced code disclosure is open
    And I author a palette-test convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I open the component palette
    Then the component palette lists "Badge" in the "This design" group
    And the component palette lists "SetTable" in the "Library" group
    And the component palette lists "ConfirmButton" in the "Library" group


  @m12 @single-user
  Scenario: Inserting a design component into a selected element adds it as the last child, selected, and the canvas expands it
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "palettechild"
    And I edit the design "palettechild"
    When I add a type to the design
    And I name the just-added type "Db"
    When I add a field "note" to the type "Db"
    When I ensure the Advanced code disclosure is open
    And I author a palette-test convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I click the tree editor's "main" element row
    Then the tree editor's "main" element row is selected
    When I open the component palette
    And I click the palette item "Badge"
    Then the tree editor's "Badge" element row is the last child of the "main" element row
    And the tree editor's "Badge" element row is selected
    And the design canvas shows a "span" element reading "Badge"


  @m12 @single-user
  Scenario: Inserting a library component renders it literally on the canvas
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "palettelib"
    And I edit the design "palettelib"
    When I add a type to the design
    And I name the just-added type "Db"
    When I add a field "note" to the type "Db"
    When I ensure the Advanced code disclosure is open
    And I author a palette-test convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I open the component palette
    And I click the palette item "SetTable"
    Then the tree editor's "SetTable" element row is the last child of the "main" element row
    And the design canvas contains a literal "SetTable" element
