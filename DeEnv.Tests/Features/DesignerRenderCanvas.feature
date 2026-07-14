Feature: Designer - Structured Render Tree and Canvas


  # /instances/<id> is ONLY a selector: a <select> dropdown of the designs, the instance's current
  # design pre-selected (the explicit reference read back through the <select> binding).
  @milestone-10 @single-user
  Scenario: The instance page is a design selector with the current design pre-selected
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the instances list
    And I open the instance "todo"
    Then the design dropdown has the design "todo" selected


  # The create form's design picker is the generic <RefSelect> — a BARE ref-binding <select> in the lib,
  # no Set/Use button. Picking an option fires the native change → RefSelect's onChange (applyPick) →
  # sys.setRef on the draft (the write is in HANDLER position, not render). So a single native pick (no
  # extra click) binds the draft's design, and Save spawns the instance running it. This proves the
  # render-time sys.setRef the old picker used is gone, replaced by the generic component.
  @milestone-10 @single-user
  Scenario: The create form picks a design through the generic RefSelect with no extra button
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the instances list
    And I reveal the instance create form
    Then the instance create form has a bare design ref-select with no Set button
    When I pick the design "todo" in the create form and name it "picked" and save
    Then a new instance "picked" running design "todo" appears in the instances list
    When I open that new instance
    Then the design dropdown has the design "todo" selected


  # db.instances is kept in lockstep with host actions: create/delete/rename/clone/setDesign all
  # mirror their kernel-registry write into the design-host's db.instances extent. Slice 2 tests
  # the three most directly observable: create (INSERT row), delete (REMOVE row), rename (UPDATE name).
  # These are STORE reads — no UI assertion; the UI slice (Slice 3) drives the browser.
  @milestone-10 @single-user
  Scenario: Creating an instance via a host action inserts a row in db.instances
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When a new instance named "newapp" is created from the "todo" design via host action
    Then the design-host has a stored Instance named "newapp"
    And the stored Instance "newapp" has a runtimeId that matches the new kernel instance


  # ──── M12 X2b — the "Convert to structured" button + the structured render view ──────────────────────────────────
  #
  # X2a wired sys.importRender(design) (a server-only, admin-gated host action that converts a design's
  # text `ui` render into structured MetaNode rows and clears `ui`); nothing called it. X2b makes the
  # foundation USABLE from the editor: the Advanced code block shows a "Convert render to structured"
  # button ONLY for a TEXT-authored design (a non-empty `ui`, an empty `render` set), and — once
  # converted — shows the structured MetaNode rows as a FIRST-CLASS "Structured render" section (OUTSIDE
  # the collapsing Advanced disclosure, so a successful convert is immediately visible — review fix: the
  # disclosure's open/closed state is uncontrolled DOM, and the convert ack's re-render was collapsing it,
  # making a successful convert look like nothing happened). The two modes are exclusive (the S1a
  # precedence gate: a design's render is EITHER text OR structured, never both), so the editor shows one
  # or the other; the `ui` textarea + Convert button stay under Advanced for a text design.
  #
  # Once structured, the render shows as the TREE EDITOR (M12 E1, replacing X2b's read-only SetTable): a
  # recursive `renderNodeEditor` component renders the imported root element with an editable `tag` input.
  # No add/remove/reorder this slice (that's E2), so there is no create/remove affordance to guard — the
  # tree editor simply has no such control.
  #
  # The proof authors a SIMPLE convertible render (an element tree with an attribute + a text child — the
  # shape S1b's import accepts; the seeded todo render uses foreach/helpers and is deliberately NOT
  # importable) into a fresh design's `ui`, converts it, and asserts the mode flipped: the `ui` textarea is
  # gone and the tree editor — now visible without reopening any disclosure — shows the imported ROOT
  # element with its `tag` input reading "main". The convert is a host action; its ack refetch re-renders
  # the editor, flipping the mode — polled via the tree editor's appearance, no fixed sleep.
  @m12 @single-user
  Scenario: A text-authored design shows a Convert button that converts it to the structured tree editor
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "convertme"
    And I edit the design "convertme"
    And I expand the Advanced code disclosure
    And I author a simple convertible render into the design's UI
    Then the design editor shows the Convert-to-structured button
    And the design editor shows the design's UI text in a textarea
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    And the tree editor's root node tag input reads "main"
    And the design editor no longer shows the UI textarea
    And the design editor no longer shows the Convert-to-structured button


  # ──── M12 E1 — the structured-render TREE EDITOR: recursive, inline scalar editing ────────────────────────────────────
  #
  # X2b left the structured render as a read-only ONE-ROW SetTable (root only, no way to see or edit the
  # tree). E1 turns it into a real editor. The crux (a load-bearing finding for the whole canvas track,
  # S4/S5): the designer had never used a SELF-RECURSIVE render component. `renderNodeEditor(node)` renders
  # `node` and, inside a keyed `foreach child in node.children`, invokes `<renderNodeEditor node={child}>`
  # again — a component that renders ITSELF for descendants, to arbitrary depth. The foreach already pushes
  # each child's id onto the slot path (executeTagForEach), so each recursion gets a distinct, stable slot
  # key per node — no collision, no explicit key= needed; the data tree is finite, so recursion terminates.
  # This scenario runs that recursion through the REAL render path (SSR + a browser DOM assertion): the
  # nested `<h1>` must appear NESTED under `<main>`, proving the component recursed a level deep.
  #
  # Each node's scalar fields are two-way-bound inputs, exactly like the type/prop editor: an ELEMENT node
  # (non-empty tag) shows an editable `tag` input, its attrs (name/value inputs), then its children
  # (recursed, indented); a LEAF node (empty tag) shows an editable `expr` input. Editing is an ordinary
  # ctx field write on the MetaNode/MetaAttr; projection reads the edited fields, so after an edit the
  # design still PROJECTS to a valid `fn render()`. This slice is SCALAR EDITING ONLY — add/remove/reorder
  # of nodes and attrs is deferred to E2; the single-root invariant is kept (no root-level add/remove).
  #
  # The proof converts a design whose render is <main class="x"><h1>{leaf}</h1></main> (an element with a
  # nested element whose child is a text-expression leaf). The tree editor then shows the nested structure
  # (the h1's tag input nested under main; the leaf's expr shown). Editing the root's tag input from "main"
  # to "section" persists (store poll) AND the design still projects — re-opening the editor round-trips the
  # new tag. Auto-waiting locators / store polls throughout, no fixed sleep.
  @m12 @single-user
  Scenario: The structured render tree editor recurses to show nesting and inline-edits a node's tag with a valid round-trip
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "treeme"
    And I edit the design "treeme"
    And I expand the Advanced code disclosure
    And I author a nested convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    And the tree editor's root node tag input reads "main"
    And the tree editor shows a nested node with tag input "h1"
    And the tree editor shows a leaf expr input reading "leaf"
    When I edit the root node's tag input to "section"
    Then the stored render root node has tag "section"
    When I open the designs list
    And I edit the design "treeme"
    Then the design editor eventually shows the structured render tree editor
    And the tree editor's root node tag input reads "section"


  # ──── M12 E2 — the structured-render tree editor becomes STRUCTURALLY editable ──────────────────────────────────────
  #
  # E1 made the tree editor recurse and inline-edit each node's SCALAR fields, but you could not change the
  # SHAPE of the tree — no way to add or remove nodes/attributes. E2 adds that, mirroring the type editor's
  # add/remove idiom (set.add({…all fields defaulted…}) + an inline set.remove(member)). Each ELEMENT node
  # gets a small button row — "+ element" / "+ text" / "+ attr" — that appends a child element (default tag
  # "div"), a child text-leaf (expr defaulting to the empty-string literal source "" so it PROJECTS), or an
  # attribute (value likewise "" so it projects). Each non-root child and each attr gets an inline "×" that
  # removes it from its parent's set (the removed subtree is GC-reclaimed). The single-root invariant holds:
  # the ROOT keeps its add controls but has NO remove control.
  #
  # The one real correctness trap: E1 renders children via .orderBy(c => c.order), and the import assigns
  # dense 0,1,2… orders, so a naive order:0 on a new child would SORT TO THE FRONT and collide with the
  # imported first child. New members must APPEND — order = (max existing sibling order) + 1, computed in
  # Code over the sibling set (orderBy descending, take the first). The scenario proves it: after adding an
  # element to the root (whose sole imported child is <h1>), the new node lands LAST, not first.
  #
  # The proof: convert the nested render, add an element child to the root (assert it appears nested and
  # LAST), edit its tag, add an attribute to it, add a text child, then REMOVE that added element — and the
  # design still projects to a valid fn render() (re-open round-trips). Auto-waiting locators / store polls
  # throughout, no fixed sleep.
  @m12 @single-user
  Scenario: The structured render tree editor adds and removes child nodes and attributes, appending in order
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "treeme"
    And I edit the design "treeme"
    And I expand the Advanced code disclosure
    And I author a projectable nested render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    And the tree editor's root node tag input reads "main"
    # A projectable app document needs a Db root type — give this create-form design one (with a `greeting`
    # field the imported leaf `db.greeting` binds to) so the projection assertions below check the RENDER's
    # structural validity, not an incidental missing-schema or unbound-symbol error.
    When I add a type to the design
    And I name the just-added type "Db"
    And I add a field "greeting" to the type "Db"
    When I add a child element to the root node
    Then the root node's last child is an element with tag "div"
    When I edit the root node's last child tag input to "footer"
    Then the root node's last child is an element with tag "footer"
    When I add an attribute to the root node's last child
    And I add a text child to the root node's last child
    Then the root node's last child element has an attribute input and a text-leaf child
    And the stored render projects to a valid design document
    When I remove the root node's last child
    Then the root node no longer has a child element with tag "footer"
    And the stored render projects to a valid design document


  # ──── M12 S5a — reorder (▲/▼ swap `order` with the neighbor sibling; the E2 add/remove idiom family) ──
  #
  # The one structural op E2 still lacked (visual-designer.md's E3 ledger). `moveRow(coll, node, dir)` finds
  # the nearest sibling by strict `order` comparison and swaps the two ints — an ordinary two ctx-staged
  # writes, no new builtins/twins (`order` is already a dep-recorded row field both the tree editor's
  # `orderBy` and the canvas's `renderTree` walk read, so a swap repaints BOTH surfaces same-frame).
  #
  # The proof: convert the nested render (root <main> starts with one child, <h1>), append two more elements
  # and rename them, giving the root exactly three children (h1, second, third — test (a)'s "parent with
  # three children"); assert the tree editor AND the canvas already agree on that order; assert the first
  # row's ▲ and the last row's ▼ are DISABLED (test (b) — ux review: disable-in-place, not hidden — the
  # button is always present, only its `disabled` attribute reflects the edge, so × never slides into the
  # slot a chase-click would land on); click ▼ on the first row and assert BOTH surfaces show the new order
  # with no reload (the same-frame repaint pin); reload the whole editor and assert the new order survived —
  # proving the swap is a real persisted write, not just an optimistic client reorder (test (c)).
  @m12 @single-user
  Scenario: The tree editor reorders sibling nodes, the canvas repaints same-frame, and the new order survives a reload
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "treeme"
    And I edit the design "treeme"
    And I expand the Advanced code disclosure
    And I author a projectable nested render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I add a type to the design
    And I name the just-added type "Db"
    And I add a field "greeting" to the type "Db"
    When I add a child element to the root node
    And I edit the root node's last child tag input to "second"
    And I add a child element to the root node
    And I edit the root node's last child tag input to "third"
    Then the root node's children read, in order: "h1, second, third"
    And the design canvas shows children in order: "h1, second, third"
    And the root node's first child's move-up button is disabled
    And the root node's last child's move-down button is disabled
    And I capture a screenshot named "s5a-reorder-column"
    And the stored render projects to a valid design document
    When I click move-down on the root node's child 0
    Then the root node's children read, in order: "second, h1, third"
    And the design canvas shows children in order: "second, h1, third"
    And the stored render projects to a valid design document
    And the root node's children are persisted in order: "second, h1, third"
    When I reload the design editor
    Then the root node's children read, in order: "second, h1, third"


  # ──── M12 S5c — unwrap (splice a plain element's children into its own parent collection) ────────────────
  #
  # The half of wrap/unwrap that composes cleanly on the MOVE primitive (link an existing object into an
  # ALREADY-EXISTING set, then unlink it from its old one — grounded + landed as the "arrayAdd refId"
  # branch): unwrap's children and its target parent collection both already exist, so nothing is ever
  # minted. (WRAP needs the opposite — mint a brand-new container and populate ITS OWN nested set in the
  # same handler — which the object model cannot do synchronously today: a just-minted set-typed prop has
  # no real backing id until its OWN owner's create round-trip returns, so anything added to it before
  # then is silently dropped, never reaching the wire at all. Wrap is deliberately NOT built this slice —
  # it needs its own wire-surface decision.)
  #
  # The proof: <main><section><h1>"Title"<p>"Body"</section><footer>"Bye"</footer></main>. Unwrapping
  # <section> must splice h1+p into <main>'s own children at section's former position (section itself is
  # discarded), the tree editor AND the canvas repaint same-frame with the new flat order, h1/p keep their
  # EXACT stored ids (the identity pin — a move, never a mint-a-copy-and-abandon-the-original), and the
  # new shape survives a reload.
  @m12 @single-user
  Scenario: Unwrapping a mid-tree element splices its children into the parent, preserves their identity, repaints same-frame, and survives a reload
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "unwrapme"
    And I edit the design "unwrapme"
    And I expand the Advanced code disclosure
    And I author an unwrap-test convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    And the root node's children read, in order: "section, footer"
    When I capture the stored id of the MetaNode with tag "h1"
    And I capture the stored id of the MetaNode with tag "p"
    And I click unwrap on the root node's child 0
    Then the root node's children read, in order: "h1, p, footer"
    And the design canvas shows children in order: "h1, p, footer"
    And no MetaNode has tag "section"
    And the MetaNode with tag "h1" still carries its captured id
    And the MetaNode with tag "p" still carries its captured id
    When I reload the design editor
    Then the root node's children read, in order: "h1, p, footer"


  # The ROOT case: a root whose exactly-one child is an ELEMENT can unwrap — that child becomes the new
  # sole root, keeping its OWN id (the shape a hand-authored "undo my wrap" needs, since wrap itself isn't
  # built — the fixture stands in for what wrap would have produced).
  @m12 @single-user
  Scenario: Unwrapping the sole root replaces it with its one element child as the new root, keeping its identity
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "unwraproot"
    And I edit the design "unwraproot"
    And I expand the Advanced code disclosure
    And I author a wrapped-root convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I capture the stored id of the MetaNode with tag "button"
    And I click unwrap on the root node
    Then no MetaNode has tag "div"
    And the design "unwraproot"'s render root has the captured id of tag "button"


  # The tie-scramble regression (arch review fold): a reorder INSIDE the wrapped element (moveRow swaps
  # `order` values, not ids) must survive splicing. The live client's stable sort masks a shared-order tie
  # by array-insertion order, but the DURABLE paths (SchemaBridge.OrderedMembers — the commit/publish
  # projection walk — and the store reload) tie-break by intrinsic id instead, silently reverting the
  # reorder in the published document even though the live tree editor and canvas still agree with each
  # other. Fixed by densely renumbering the parent collection (0..n-1 by current visual order) right after
  # splicing — restoring the distinct-order invariant unwrap's tie briefly broke.
  @m12 @single-user
  Scenario: A reorder inside the wrapped element survives unwrap through a reload and the projected document
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
    Then the root node's child 0's children read, in order: "h1, p"
    When I click move-down on the root node's child 0's child 0
    Then the root node's child 0's children read, in order: "p, h1"
    When I click unwrap on the root node's child 0
    Then the root node's children read, in order: "p, h1, footer"
    And the design canvas shows children in order: "p, h1, footer"
    And the root node's children are persisted in order: "p, h1, footer"
    And no MetaNode has tag "section"
    When I reload the design editor
    Then the root node's children read, in order: "p, h1, footer"
    And the projected document shows "p" before "h1" in the render


  @m12 @single-user
  Scenario: Wrapping an existing node preserves its identity and survives reload
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "unwrapme"
    And I edit the design "unwrapme"
    And I expand the Advanced code disclosure
    And I author an unwrap-test convertible render into the design's UI
    And I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I add a type to the design
    And I name the just-added type "Db"
    And I add a field "greeting" to the type "Db"
    When I capture the stored id of the MetaNode with tag "footer"
    And I click wrap on the root node's child 1
    Then the root node's children read, in order: "section, div"
    And the root node's child 1's children read, in order: "footer"
    And the MetaNode with tag "footer" still carries its captured id
    When I reload the design editor
    Then the root node's children read, in order: "section, div"
    And the root node's child 1's children read, in order: "footer"
    And the projected document shows "div" before "footer" in the render


  # ──── M12 CANVAS-1 — the CLIENT-COMPUTABLE canvas (sys.renderTree) ────────────────────────────────────────────────────────────
  #
  # The tree editor (E1/E2) edits the render as DATA; the canvas is the paired VIEW of that data — a live
  # rendered tag tree the operator watches change as they edit. Unlike the S3a Preview (a server-backed read
  # of the design's REAL evaluated render, refreshed on demand), the canvas is `sys.renderTree(node)` computed
  # by BOTH twins from the MetaNode rows the client already holds — so it repaints INSTANTLY as the tree editor
  # mutates, with no server round-trip. This is the surface S4 turns into the visual editor, so its contract
  # carries three baked-in guards proven here: (1) data-node provenance on every emitted element (the future
  # click-to-select spine); (2) expressions that can't evaluate client-side yet show as span.expr-chip
  # placeholders; (3) the walk goes through dep-recording reads, so an edit re-renders the canvas live.
  #
  # The proof converts <main class="x"><h1>{leaf}</h1></main> (leaf is the bare symbol `leaf` — a NON-literal,
  # so it renders as a chip). The canvas then shows a <main> and a nested <h1>, each carrying data-node, and a
  # chip reading "leaf". THE LIVENESS PROOF: editing the root's tag input in the tree editor flips the canvas's
  # <main> to <section> with NO reload; adding a child element makes a <div> appear in the canvas — both in the
  # same interaction, proving dep-recording fires through renderTree's row walk. Auto-waiting locators, no sleep.
  @m12 @single-user
  Scenario: The canvas renders the structured render live and updates as the tree is edited, with no reload
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "treeme"
    And I edit the design "treeme"
    And I expand the Advanced code disclosure
    And I author a nested convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    And the design canvas shows a "main" element with a data-node attribute
    And the design canvas shows a "h1" element with a data-node attribute
    And the design canvas shows an expression chip reading "leaf"
    When I edit the root node's tag input to "section"
    Then the design canvas shows a "section" element with a data-node attribute
    When I add a child element to the root node
    Then the design canvas shows a "div" element with a data-node attribute


  # ──── M12 CANVAS-EVAL-1 — the canvas EVALUATES expressions (sys.evalContext) ──────────────────────────────────────────
  #
  # CANVAS-1 rendered a non-literal leaf/attr as an inert chip (display-only, no evaluation). This slice
  # wires `sys.renderTree(node, sys.evalContext(design, evalRefresh))`: the server ships a SYNTHETIC `db`
  # seed graph (the design's own `initialData`, re-minted) plus a content-addressed map of PARSED expression
  # ASTs, and the walk runs each non-literal leaf through the REAL interpreter over that seed — so the
  # canvas shows the design's actual evaluated output, not a placeholder.
  #
  # The schema (a `Db` type with `greeting`/`greeting2` fields) and the `initialData` seed are authored
  # BEFORE the render tree exists, so the render section's FIRST-EVER appearance (right after Convert) already
  # has a complete, valid schema+data — the evalContext's first compute succeeds outright, sidestepping any
  # question of whether an ordinary field edit alone forces a fresh eval (deliberately, it does not — only an
  # explicit Refresh does; see below).
  #
  # THE RACE GUARD: editing the leaf's expr text (a plain optimistic tree-editor mutation, no server round
  # trip) must fall the canvas to a HONEST chip showing the NEW source — same frame, no refetch storm — and
  # must NOT disturb the tree editor's own input (still reads the edited text, not reverted). Clicking
  # "Refresh values" is the ONLY thing that re-evaluates. A later STRUCTURAL edit (renaming the root tag) must
  # repaint the structural part same-frame WITHOUT touching the (unrelated, still-cached) evaluated leaf — no
  # chip flicker on it.
  @m12 @single-user
  Scenario: The canvas evaluates expressions against the design's seed data, chips an edited expression until Refresh, and never flickers an evaluated leaf on a structural edit
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "treeme"
    And I edit the design "treeme"
    When I add a type to the design
    And I name the just-added type "Db"
    And I add a field "greeting" to the type "Db"
    And I add a field "greeting2" to the type "Db"
    When I ensure the Advanced code disclosure is open
    And I set the design's initial data to:
      """
      initialData
          Db 1
              greeting: "Hello"
              greeting2: "World"
      """
    When I ensure the Advanced code disclosure is open
    And I author a projectable nested render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    And the design canvas shows the evaluated leaf text "Hello"
    When I edit the leaf expr input to "db.greeting2"
    Then the design canvas shows an expression chip reading "db.greeting2"
    And the tree editor shows a leaf expr input reading "db.greeting2"
    When I click Refresh values
    Then the design canvas shows the evaluated leaf text "World"
    When I edit the root node's tag input to "section"
    Then the design canvas shows a "section" element with a data-node attribute
    And the design canvas shows the evaluated leaf text "World"


  # ──── M12 auto-live parse-op — the canvas evaluates a NEWLY EDITED expression WITHOUT "Refresh values" ────
  #
  # CANVAS-EVAL-1 proved the canvas evaluates against a SHIPPED evalContext, but an edited-but-unrefreshed
  # expression falls to an honest chip until the operator clicks "Refresh values" — the S3a-race-inversion's
  # deliberate empty-deps law. This slice closes that last gap WITHOUT reopening the race: a NEW `parseExprs`
  # WS request/response op (WsHandler.cs/ws.ts) parses a newly-typed expression on demand — pure, store-free,
  # no refetch — and merges the resulting AST straight into the SAME evalContext object the canvas already
  # holds (mutating its `exprs` map in place; never re-keying evalContext's own memo, never touching
  # needsServerData). Because the round trip involves NO refetch, it cannot race the tree editor's own
  # optimistic mutations by construction — a structural edit fired immediately after typing lands untouched.
  #
  # The proof: edit the leaf to a NEW valid expression and watch the canvas evaluate it with NO Refresh click
  # (a plain poll — this only passes if the auto-live merge actually ran); edit to an INVALID expression and
  # confirm the canvas falls to an honest chip while the page keeps working (no crash — proven by every
  # subsequent step still succeeding); fix it to a DIFFERENT fresh valid expression and watch it evaluate
  # again, still with no Refresh; then, as the RACE PIN, retype the leaf to yet another fresh valid
  # expression and IMMEDIATELY (no wait in between) fire an unrelated structural edit (add a child to the
  # root) — both the tree editor's own edit and the structural addition must land untouched, and the canvas
  # must still end up evaluating the leaf's latest text.
  @m12 @single-user
  Scenario: The canvas evaluates a newly edited expression live without clicking Refresh, degrades honestly on invalid text, and never races a concurrent structural edit
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "treeme"
    And I edit the design "treeme"
    When I add a type to the design
    And I name the just-added type "Db"
    And I add a field "greeting" to the type "Db"
    And I add a field "greeting2" to the type "Db"
    When I ensure the Advanced code disclosure is open
    And I set the design's initial data to:
      """
      initialData
          Db 1
              greeting: "Hello"
              greeting2: "World"
      """
    When I ensure the Advanced code disclosure is open
    And I author a projectable nested render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    And the design canvas shows the evaluated leaf text "Hello"
    When I edit the leaf expr input to "db.greeting2"
    Then the design canvas shows the evaluated leaf text "World"
    When I edit the leaf expr input to "db.greeting +"
    Then the design canvas shows an expression chip reading "db.greeting +"
    When I edit the leaf expr input to "db.greeting != db.greeting2 ? db.greeting2 : db.greeting"
    Then the design canvas shows the evaluated leaf text "World"
    When I edit the leaf expr input to "db.greeting == db.greeting ? db.greeting2 : db.greeting"
    And I add a child element to the root node
    Then the root node's last child is an element with tag "div"
    And the tree editor shows a leaf expr input reading "db.greeting == db.greeting ? db.greeting2 : db.greeting"
    And the design canvas shows the evaluated leaf text "World"


  # ──── M12 S6a — `foreach`/`if` become structured ROWS (rows + canvas template mode) ──────────────────────────────
  #
  # A `foreach` render form now imports to a `kind="for"` MetaNode row (item + collection, body under
  # `children`) instead of being refused. The tree editor gets a matching for-row editor (item/collection
  # inputs, recursive body, its own "+ for"/"+ if"/"+ element"/"+ text" add-row); the canvas (NO-CTX in
  # S6a — the loop is NOT evaluated, that is S6b) renders the row as a MARKED TEMPLATE: a badge showing the
  # item var name plus the collection SOURCE as an (honest, unevaluated) expression chip, with the body
  # rendered once underneath. The proof: convert a render whose root has one `foreach` child, see the
  # for-template badge + chip + tree-editor inputs, edit the item/collection inputs and watch the canvas
  # repaint live (no reload — the SAME dep-recording renderTree already proved for elements), then use the
  # root's own "+ for" control to add a second loop and remove it again — proving subtree GC reaches a
  # for-row exactly like an element.
  @m12 @single-user
  Scenario: A foreach render imports to a structured for row, the canvas shows it as a marked template, and it can be added/removed
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "treeme"
    And I edit the design "treeme"
    And I expand the Advanced code disclosure
    And I author a for-loop convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    And the tree editor shows a for row with item "note" and collection "db.notes"
    And the design canvas shows a for-template with item "note"
    And the design canvas shows an expression chip reading "db.notes"
    When I edit the for row's item input to "row"
    Then the design canvas shows a for-template with item "row"
    When I edit the for row's collection input to "db.items"
    Then the design canvas shows an expression chip reading "db.items"
    And the render tree has 1 for row
    When I add a for loop to the root node
    Then the root node's last child is a for row
    And the render tree has 2 for rows
    When I remove the root node's last child for row
    Then the render tree has 1 for row


  # ──── M12 S6b — the canvas EVALUATES for/if rows (row-scope evaluation) ────────────────────────────────────────────────────────
  #
  # S6a rendered a for/if row as a NO-CTX marked TEMPLATE (badge + collection chip; both if branches).
  # S6b, with the eval context present (the canvas always passes `sys.evalContext(design, evalRefresh)`),
  # EVALUATES the row: a `for` iterates its collection against the seed graph and instantiates the body
  # PER ITEM with the loop var bound (the row scope — an ambient-bindings layer over {db}); an `if`
  # evaluates its condition and renders ONLY the taken branch. The instances REPLACE the template — real
  # content, no badge. This is the end-to-end integration over a REAL seed graph (initialData → the
  # evalContext's synthetic db), the piece the conformance suite pins on both twins at the value level.
  #
  # The design: a Db root with `notes` (a set of Note{title}) and a bool `flag`, seeded with two notes
  # ("Alpha","Beta") and flag=true; a render whose <main> holds `foreach note in db.notes → <li>{note.title}`
  # plus `if db.flag → <p>"ON" else <p>"OFF"`. After Convert the canvas shows BOTH titles as real <li> text
  # (not chips, not a for-template badge) and the taken `if` branch ("ON", never "OFF").
  #
  # THE RACE GUARD (the S3a idiom, now for a collection): editing the for-row's collection to a source the
  # shipped AST map does not carry falls the canvas to the S6a template (honest — never guesses) WITHOUT
  # disturbing the tree editor's own input, until "Refresh values" bumps the refresh key so the server re-
  # ships the new source's AST and the loop evaluates again. A later STRUCTURAL edit (root tag rename)
  # repaints same-frame with the evaluated items intact.
  @m12 @single-user
  Scenario: The canvas evaluates a foreach against the seed data, shows both items and the taken if-branch, and falls a loop to its template until Refresh
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "loopme"
    And I edit the design "loopme"
    When I add a type to the design
    And I name the just-added type "Db"
    When I add a type to the design
    And I name the just-added type "Note"
    And I add a field "title" to the type "Note"
    When I add a field "notes" to the type "Db"
    And I add a field "flag" to the type "Db"
    # Reload so the prop rows re-render via SSR — a client-added row's type/cardinality <select>s draw their
    # options from module-level `var` arrays (scalarTypes / cardinalities) that only populate on a server
    # render, so the select-based edits below must run against SSR-rendered rows (a pre-existing designer trait).
    When I reload the design editor
    And I retype the prop "notes" to "Note"
    And I set the prop "notes" cardinality to "set"
    And I retype the prop "flag" to "bool"
    When I ensure the Advanced code disclosure is open
    And I set the design's initial data to:
      """
      initialData
          Db 1
              notes: [2, 3]
              flag: true
          Note 2
              title: "Alpha"
          Note 3
              title: "Beta"
      """
    When I ensure the Advanced code disclosure is open
    And I author a for-and-if convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    And the design canvas shows a "li" element reading "Alpha"
    And the design canvas shows a "li" element reading "Beta"
    And the design canvas shows a "p" element reading "ON"
    And the design canvas does not show the text "OFF"
    When I edit the for row's collection input to "db.notes.orderBy(n => n.title)"
    Then the design canvas shows a for-template with item "note"
    And the tree editor shows a for-collection input reading "db.notes.orderBy(n => n.title)"
    When I click Refresh values
    Then the design canvas shows a "li" element reading "Alpha"
    When I edit the root node's tag input to "section"
    Then the design canvas shows a "section" element with a data-node attribute
    And the design canvas shows a "li" element reading "Alpha"


  # ──── M12 F3 — call-position evaluation of design fns ────────────────────────────────────────────────────────────────────────────────────
  #
  # F2 made the canvas EXPAND a component tag; F3 makes it EVALUATE a fn called in EXPRESSION
  # position (`{fmtGreeting(db.greeting)}`, not a tag invocation) — ctx.fns binds the design's fns as
  # real callables into the isolated eval scope, so the REAL interpreter computes the value (not a
  # chip). THE STALENESS PROOF (F3b): ctx.fns is a snapshot taken when evalContext was last computed,
  # so editing the helper's BODY changes no call-site text — the canvas shows a visible banner rather
  # than silently keeping the stale value, while the UNRELATED F2 expansion (row-walk, not ctx-gated)
  # keeps updating live throughout. Refresh values recomputes the ctx and clears the banner.
  @m12 @single-user
  Scenario: The canvas evaluates a call-position helper for real, flags staleness on a body edit without disturbing live F2 expansions, and Refresh clears it
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "calleval"
    And I edit the design "calleval"
    When I add a type to the design
    And I name the just-added type "Db"
    When I add a type to the design
    And I name the just-added type "Note"
    And I add a field "title" to the type "Note"
    When I add a field "greeting" to the type "Db"
    When I add a field "notes" to the type "Db"
    When I reload the design editor
    And I retype the prop "notes" to "Note"
    And I set the prop "notes" cardinality to "set"
    When I ensure the Advanced code disclosure is open
    And I set the design's initial data to:
      """
      initialData
          Db 1
              greeting: "World"
              notes: [2]
          Note 2
              title: "Alpha"
      """
    When I ensure the Advanced code disclosure is open
    And I author a call-eval convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    And the design canvas shows a "span" element reading "Hi World"
    And the design canvas shows a "li" element reading "Alpha"
    And the design canvas does not show the stale-fns banner
    When I edit the component "fmtGreeting"'s body leaf to "\"Hello \" + name"
    Then the design canvas shows the stale-fns banner
    And the design canvas shows a "li" element reading "Alpha"
    When I click Refresh values
    Then the design canvas shows a "span" element reading "Hello World"
    And the design canvas does not show the stale-fns banner


  # ──── M12 S4a — canvas selection: click → select → highlight → scroll-to-row ──────────────────────────────────────────────
  #
  # The canvas becomes the editor's selection surface: every element sys.renderTree emits carries
  # `data-node` (CANVAS-1); a click resolves the nearest one and writes it into the `selectedNode` ui
  # var, which BOTH sides read — the canvas (a client chrome post-pass toggling `is-selected` on every
  # matching [data-node] element) and the tree editor (renderNodeEditor's own reactive class). A click on
  # the SAME element twice, or an unrelated structural edit, must never disturb an existing selection;
  # clicking empty canvas space (off any data-node element) deselects.
  @m12 @single-user
  Scenario: Clicking a canvas element selects its row, highlights both sides, scrolls it into view, survives an unrelated edit, and clicking empty canvas deselects
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "selectme"
    And I edit the design "selectme"
    When I ensure the Advanced code disclosure is open
    And I author a selection-test convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    And the design canvas shows a "h1" element reading "Hello"
    When I click the design canvas "h1" element reading "Hello"
    Then the tree editor's "h1" element row is selected
    And the design canvas shows 1 selected element
    And the selected tree editor row is scrolled into view
    When I edit the root node's tag input to "section"
    Then the design canvas shows a "section" element with a data-node attribute
    And the tree editor's "h1" element row is selected
    When I click empty canvas space
    Then the design canvas shows 0 selected elements
    And no tree editor row is selected


  # N:1 — a loop's rendered instances all share the ONE body row's data-node (S6a), so clicking ANY
  # instance selects that shared template row, and EVERY instance sharing it outlines together.
  @m12 @single-user
  Scenario: Clicking a loop instance in the canvas selects the shared template row, and every instance outlines together
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "loopme"
    And I edit the design "loopme"
    When I add a type to the design
    And I name the just-added type "Db"
    When I add a type to the design
    And I name the just-added type "Note"
    And I add a field "title" to the type "Note"
    When I add a field "notes" to the type "Db"
    And I add a field "flag" to the type "Db"
    When I reload the design editor
    And I retype the prop "notes" to "Note"
    And I set the prop "notes" cardinality to "set"
    And I retype the prop "flag" to "bool"
    When I ensure the Advanced code disclosure is open
    And I set the design's initial data to:
      """
      initialData
          Db 1
              notes: [2, 3]
              flag: true
          Note 2
              title: "Alpha"
          Note 3
              title: "Beta"
      """
    When I ensure the Advanced code disclosure is open
    And I author a for-and-if convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    And the design canvas shows a "li" element reading "Alpha"
    And the design canvas shows a "li" element reading "Beta"
    When I click the design canvas "li" element reading "Alpha"
    Then the tree editor's "li" element row is selected
    And the design canvas shows 2 selected elements


  # ──── M12 S4a review fold (ux finding 4) — anchor-containment ──────────────────────────────────────────────────────────────────────────────
  #
  # S4a made a canvas click first-class (a document-level delegated listener), so a literal `<a href>`
  # rendered inside the canvas — legal content, the walk never strips a real href — must not let that
  # click escape into interceptNavigation's own document-level listener and take the whole designer
  # page away. The click should SELECT the anchor's own row instead, exactly like any other element.
  @m12 @single-user
  Scenario: Clicking a literal-href anchor in the canvas selects its row instead of navigating the designer away
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "selectanchor"
    And I edit the design "selectanchor"
    When I ensure the Advanced code disclosure is open
    And I author an anchor convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    And the design canvas shows a "a" element reading "Link"
    When I note the current page URL
    And I click the design canvas "a" element reading "Link"
    Then the tree editor's "a" element row is selected
    And the page URL is unchanged


  # ──── M12 S4b — bidirectional selection: a tree-row click selects too, no scroll jump ────────────────────────────────
  #
  # S4a made the CANVAS the click surface; S4b makes the TREE EDITOR one too — an ordinary deenv
  # handler (`onClick={() => selectNode(node)}` on renderNodeEditor's own row div, writing the
  # `selectedNode` ui var directly) rather than the client-side writeSelectedNode path a canvas click
  # uses. Both sides read the SAME var reactively (nodeClass on the tree side, applySelectionChrome on
  # the canvas side), so a row click highlights both — but since it never goes through
  # writeSelectedNode, it never arms the S4a scroll-to-row pass: the operator is already at the row
  # they clicked, so nothing should scroll.
  @m12 @single-user
  Scenario: Clicking a tree editor row selects it and highlights the matching canvas element, without scrolling the page
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "rowselect"
    And I edit the design "rowselect"
    When I ensure the Advanced code disclosure is open
    And I author a selection-test convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    And the design canvas shows a "h1" element reading "Hello"
    When I click the tree editor's "h1" element row
    Then the tree editor's "h1" element row is selected
    And the design canvas's "h1" element is selected
    When I note the current scroll position
    And I click the tree editor's "h1" element row
    Then the page scroll position is unchanged


  # ──── M12 S4b — nested rows: the innermost row wins ────────────────────────────────────────────────────────────────────────────────────────────────
  #
  # Rows nest (E2's onRemove-passing recursion): renderNodeEditor's own onClick sits on EVERY row's
  # div, including nested ones, and deenv's onClick wiring (ui.ts wireEvents) already stops propagation
  # as the FIRST thing a handled click does — so a click that lands on a nested row's own head is
  # handled by that row's OWN listener before the event ever reaches an ancestor row's. No extra
  # containment logic needed; this scenario pins that the runtime's native bubble-then-stop order
  # already gives the correct (innermost) answer.
  @m12 @single-user
  Scenario: Clicking a nested tree editor row selects the nested row, not its ancestor
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "nestedrows"
    And I edit the design "nestedrows"
    When I ensure the Advanced code disclosure is open
    And I author a selection-test convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    And the design canvas shows a "h1" element reading "Hello"
    When I click the tree editor's "h1" element row
    Then the tree editor's "h1" element row is selected
    And the tree editor's "main" element row is not selected
    And the design canvas's "h1" element is selected


  # ──── M12 S4b — Escape deselects, both sides ──────────────────────────────────────────────────────────────────────────────────────────────────────────────
  @m12 @single-user
  Scenario: Pressing Escape clears the selection on both the canvas and the tree editor
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "escapeme"
    And I edit the design "escapeme"
    When I ensure the Advanced code disclosure is open
    And I author a selection-test convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    And the design canvas shows a "h1" element reading "Hello"
    When I click the design canvas "h1" element reading "Hello"
    Then the tree editor's "h1" element row is selected
    And the design canvas shows 1 selected element
    When I press Escape
    Then the design canvas shows 0 selected elements
    And no tree editor row is selected


  # design.render conventionally holds exactly ONE root (fn render()'s single return — a designer-facing
  # validation, "a design's render tree may have only one root"), so "nothing selected" cannot honestly
  # fall back to design.render itself (that would mint a second root and invalidate the design). The honest
  # fallback is the SAME rule a selected root element already gets: insert as its LAST CHILD.
  @m12 @single-user
  Scenario: Inserting with nothing selected falls back to the render root's children
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "palettetop"
    And I edit the design "palettetop"
    When I add a type to the design
    And I name the just-added type "Db"
    When I add a field "note" to the type "Db"
    When I ensure the Advanced code disclosure is open
    And I author a palette-test convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I open the component palette
    And I click the palette item "Badge"
    Then the tree editor's "Badge" element row is the last child of the "main" element row
    And the tree editor's "Badge" element row is selected
    And the design canvas shows a "span" element reading "Badge"


  @m12 @single-user
  Scenario: Inserting into a selected leaf adds the new row as its sibling instead of nesting into it
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "paletteleaf"
    And I edit the design "paletteleaf"
    When I add a type to the design
    And I name the just-added type "Db"
    When I add a field "note" to the type "Db"
    When I ensure the Advanced code disclosure is open
    And I author a palette-test convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I click the tree editor's leaf row reading "\"Hello\""
    When I open the component palette
    And I click the palette item "Badge"
    Then the tree editor's "Badge" element row is the last child of the "h1" element row
    And the tree editor's "Badge" element row is selected


  # ──── M12 S5b review fold — the chain-nest trap ────────────────────────────────────────────────────────────────────────────────────────────────────────
  #
  # A component-call row (Badge, a design fn) never becomes an "into" target — inserting into it would
  # silently drop the next insert (Badge's own render has no rows the tree editor can reach). Item 1's
  # pin: two inserts from the same starting selection land as SIBLINGS, both visible on the canvas —
  # and the palette stays open across both (item 7's pin rides along, no re-opening needed).
  @m12 @single-user
  Scenario: Repeated inserts from the same starting selection build siblings, not a nested chain
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "palettechain"
    And I edit the design "palettechain"
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
    And the component palette is still open
    When I click the palette item "ConfirmButton"
    Then the tree editor's "ConfirmButton" element row is the last child of the "main" element row
    And the design canvas shows a "span" element reading "Badge"
    And the design canvas contains a literal "ConfirmButton" element


  # ──── M12 S5b review fold — for/if targeting + the honest caption ──────────────────────────────────────────────────────────────────
  #
  # A selected `for` row is unambiguous — its body IS the obvious insert target. Reuses the S6b
  # for-and-if fixture (a <main> holding one `for` row and one `if` row as siblings).
  @m12 @single-user
  Scenario: Inserting into a selected for row targets its loop body
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "loopme"
    And I edit the design "loopme"
    When I add a type to the design
    And I name the just-added type "Db"
    When I add a field "note" to the type "Db"
    When I ensure the Advanced code disclosure is open
    And I author a for-and-if convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I click the tree editor's for row
    Then the tree editor's for row is selected
    When I open the component palette
    Then the palette target caption reads "Inserts into the loop body."
    When I click the palette item "ConfirmButton"
    Then the tree editor's "ConfirmButton" element row is the last child of the for row


  # A selected `if` stays a sibling insert (then/else is genuinely ambiguous — punt honestly), but the
  # caption must not claim it "can't hold children" (false — both branches can).
  @m12 @single-user
  Scenario: Inserting with a selected if row targets its sibling position with an honest caption
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "loopme"
    And I edit the design "loopme"
    When I add a type to the design
    And I name the just-added type "Db"
    When I add a field "note" to the type "Db"
    When I ensure the Advanced code disclosure is open
    And I author a for-and-if convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I click the tree editor's if row
    Then the tree editor's if row is selected
    When I open the component palette
    Then the palette target caption reads "Inserts next to the condition (choose a branch row to insert inside)."
    When I click the palette item "ConfirmButton"
    Then the tree editor's "ConfirmButton" element row is the last child of the "main" element row


  # ──── M12 S5b review fold — the second-root edge ──────────────────────────────────────────────────────────────────────────────────────────────────────
  #
  # Nothing selected + the design's sole render root is a bare leaf (unreachable via any existing UI
  # path — seeded directly through the store): there is no element anywhere to insert into, so the
  # honest outcome is a disabled palette, not a silently-minted invalid second root. Pins BOTH the UI
  # affordance (disabled buttons, an honest caption) and the DATA-LAYER guard underneath it (a forced
  # handler invocation still no-ops).
  @m12 @single-user
  Scenario: A bare-leaf render root disables the insert honestly instead of minting an invalid second root
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "paletteleafroot"
    And I edit the design "paletteleafroot"
    When I add a type to the design
    And I name the just-added type "Db"
    When I add a field "note" to the type "Db"
    When the design "paletteleafroot"'s render root is seeded as a bare leaf, bypassing the UI
    And I reload the design editor
    Then the tree editor's top-level render row count is 1
    When I open the component palette
    Then the palette target caption reads "Select an element to insert into — the render root can't hold children."
    And the palette insert buttons are disabled
    When I force-invoke the palette item "Badge"'s click handler
    Then the tree editor's top-level render row count is 1


  # ──── M12 S5b review fold — reveal-scroll for a remote insert ────────────────────────────────────────────────────────────────────────────
  #
  # A root-fallback insert (nothing selected) can land far below the fold on a long tree with zero
  # visible confirmation otherwise. Reuses the existing S4a/S4b scroll-into-view proof.
  @m12 @single-user
  Scenario: A root-fallback insert on a long tree scrolls the new row into view
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "palettescroll"
    And I edit the design "palettescroll"
    When I add a type to the design
    And I name the just-added type "Db"
    When I add a field "note" to the type "Db"
    When I ensure the Advanced code disclosure is open
    And I author a long palette-test convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I open the component palette
    And I click the palette item "Badge"
    Then the tree editor's "Badge" element row is selected
    And the selected tree editor row is scrolled into view
