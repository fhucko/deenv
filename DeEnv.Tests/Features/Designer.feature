Feature: The operator IDE (designs library + instance design selector)
  The designer (instance 1) is a URL-routed multi-instance IDE, authored as an explicit custom
  `fn render()` over a `Db { designs: set of Design }` meta-schema. A `Design` is a WHOLE app —
  structured `types` plus the other app-document sections (initialData/common/ui) as source text. The
  surfaces are SEPARATE: `/designs` is the design LIBRARY (list + per-design edit/delete) and
  `/designs/<designId>` is the design EDITOR (type/prop editor + ui/common/initialData code areas, NO
  publish); `/instances` lists the hosted instances each showing its CURRENT design, and
  `/instances/<id>` is ONLY a design SELECTOR — a `<select>` dropdown of the designs with the
  instance's current one pre-selected + an Apply button that records the chosen design on the
  instance AND deploys it. The instance↔design link is an EXPLICIT reference: each instance stores a
  `designId` (the id of a design in the designer's `db.designs`), seeded so the dropdowns start
  correct and read back to pre-select. The seeded designs are FAITHFUL copies of the committed apps
  the kernel runs (todo, crm, shop). Driven against a REAL kernel host (the
  designer needs a non-empty `sys.instances`), through a browser. Milestone 10.

  @milestone-auth @single-user
  Scenario: The committed designer gates anonymous visitors with its own login form
    Given the anonymous operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designer designs route
    Then the committed designer login gate is shown

  @milestone-10 @single-user
  Scenario: The designs route lists the design library
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    Then the designs list shows a design "todo"
    And the designs list shows a design "crm"

  # The designs list is rendered by the generic <SetTable> (label-only column) with a per-row action
  # cell carrying an Edit link (/designs/<id>) and a Delete button. Opening the list CLIENT-SIDE (the
  # step waits for window.initUi) proves the client now hydrates over the <SetTable>-rendered page —
  # the prior build crashed on hydration (a function-equality twin divergence) and timed out here.
  @milestone-10 @single-user
  Scenario: Each designs-list row shows its label, an Edit link, and a Delete button via SetTable
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    Then the designs list shows a design "todo"
    And the design "todo" row has an Edit link and a Delete button
    And the design "crm" row has an Edit link and a Delete button

  # The designer (instance 1) is a managed instance like any other: no special-casing in the render.
  # It has its OWN design in db.designs (a bounded self-snapshot) and its instances-list row resolves
  # to that design through the same explicit designId reference every other row uses — so it appears in
  # BOTH lists uniformly. (The kernel always hosts the designer; the fixture seeds its designId.)
  @milestone-10 @single-user
  Scenario: The designer appears uniformly as a design and a managed instance
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    Then the designs list shows a design "designer"
    When I open the instances list
    Then the instances list shows the instance "designer" running design "designer"

  # The "todo" design is the REAL todo app: its types include TodoItem and its UI is the real
  # custom `fn render()` — so the editor (now at /designs/<id>) shows that app's actual content.
  @milestone-10 @single-user
  Scenario: Editing a design shows its real types and its UI text
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    Then the design editor shows a type named "TodoItem"
    And the design editor shows the design's UI text in a textarea

  # ── client-side (SPA) navigation in the CUSTOM-render designer (uniform with the generic UI) ──
  # The designer is a fully-custom `fn render()`, yet an in-app Edit-link click navigates CLIENT-SIDE
  # exactly as the generic UI does: the click is intercepted, the URL is updated via the History API,
  # and the deep `/designs/<id>` type/prop editor re-renders over the warm session — NO full page reload,
  # NO re-hydration. A window marker set after the list loads survives the navigation (a reload would wipe
  # it), the browser URL becomes the mounted editor URL, and the editor's deeply-nested content (the
  # design's real types and its UI source text — read cross-page over a fresh store load) actually renders.
  # The PRIVACY pin proves the structural-privacy claim stays honest: the designs LIST ships design labels,
  # not every design's full source — the todo design's UI source token ("user-chip") never appears in the
  # first paint's window.initData. Browser Back then restores the list (the slot-reset path re-runs the
  # list's components cleanly), also without a reload.
  @milestone-10 @single-user
  Scenario: An Edit-link click in the custom designer navigates client-side to the deep editor
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    Then the designs list first paint does not ship the design's UI source token "user-chip"
    When I mark the live page
    And I edit the design "todo"
    Then the browser URL is the mounted design editor
    And the design editor shows a type named "TodoItem"
    And the design editor shows the design's UI text in a textarea
    And the live page mark survives
    And the page is still the same hydrated session
    When I navigate back
    Then the designs list shows a design "todo"
    And the live page mark survives

  # ── no partial-content FLASH on the deep editor (round-2) ───────────────────────────────────────
  # The designs LIST ships only each design's label (structural privacy — types/ui/common/initialData are
  # NOT shipped). The design OBJECT is present (a list leaf), so the prior flash guard ("is the target
  # object present?") let the Edit-nav optimistic-paint immediately — but designEditor then reads the
  # UNSHIPPED design.types / sys.field(design,"ui"), throws "Value not available", which memoize swallows
  # to empty: the operator saw the editor heading + an EMPTY type list + blank code areas for one frame
  # before the refetch filled it (a blink on localhost; a visible "blank then snapped in" on a real
  # network). The round-2 speculative-commit guard renders the target into a throwaway tree and commits it
  # ONLY if it built completely from local data; the thin editor needs server data, so the LIST view is
  # HELD until the refetch paints the COMPLETE editor once. A MutationObserver armed before the nav records
  # any `.design-editor` that ever rendered WITHOUT a `.type-card` (the empty/partial state — the todo
  # design always has the TodoItem type, so a complete editor always has ≥1 type card), so the assertion
  # proves the partial editor never appeared, not merely that it is absent now. The populated assertions
  # (the TodoItem type + the UI text) confirm the nav still completed onto the real editor.
  @milestone-10 @single-user
  Scenario: Navigating into the deep design editor never flashes a blank editor
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I arm the blank-editor detector
    And I scroll the page down
    And I edit the design "todo"
    Then the browser URL is the mounted design editor
    And the design editor shows a type named "TodoItem"
    And the design editor shows the design's UI text in a textarea
    And the blank design editor never appeared during the navigation
    # SCROLL RESET (round-2): a full reload reset scroll; SPA forward-nav must too. The list was
    # scrolled down above, so the editor would otherwise open mid-page — assert it landed at the top.
    And the page is scrolled to the top

  # The instances list shows each instance alongside the design it currently runs, resolved by the
  # explicit designId reference (todo → its seeded "todo" design).
  @milestone-10 @single-user
  Scenario: The instances route lists the hosted instances with their current design
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the instances list
    Then the instances list shows the instance "todo" running design "todo"
    And the instances list shows the instance "crm" running design "crm"

  # /instances/<id> is ONLY a selector: a <select> dropdown of the designs, the instance's current
  # design pre-selected (the explicit reference read back through the <select> binding).
  @milestone-10 @single-user
  Scenario: The instance page is a design selector with the current design pre-selected
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the instances list
    And I open the instance "todo"
    Then the design dropdown has the design "todo" selected

  # Apply is the deploy: picking a different design and applying records it on the instance (the
  # registry designId changes) AND projects the chosen design onto the instance's app document.
  @milestone-10 @single-user
  Scenario: Applying a different design records it and deploys it to the instance
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the instances list
    And I open the instance "todo"
    And I pick the design "crm" in the dropdown
    And I apply the design
    Then the instance "todo" records the design "crm"
    And the "todo" instance's app document describes the type "Customer"

  # The end-to-end split: edit a design in /designs/<id> (rename a type + retype its reference), then
  # apply that design to its instance — the edited design is what gets deployed.
  @milestone-10 @single-user
  Scenario: Editing a design then applying it deploys the edit
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I rename the type "TodoItem" to "Widget"
    And I retype the prop "items" to "Widget"
    When I open the instance "todo"
    And I apply the design
    Then the "todo" instance's app document describes the type "Widget"

  # Create a design through the GENERIC create: the SetTable's own "New" button reveals its create form,
  # which the designs list customizes (a `createForm` slot) to a clean LABEL-ONLY field — NOT the default
  # all-scalars form (which for a Design would render ui/common/initialData as raw textareas). Save runs
  # the generic set.add(draft) and the row appears via the client re-render (no nav — race-free). Opening
  # it shows the (empty) editor with its label. An empty-types design is a valid LIBRARY entry; it is only
  # invalid to deploy until it gains types (added on the edit page).
  @milestone-10 @single-user
  Scenario: Creating a design via the generic New puts it in the library and it opens in the editor
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "blank"
    Then the designs list shows a design "blank"
    When I edit the design "blank"
    Then the design editor shows the design's label "blank"

  # Create an instance via the generic <SetTable>'s create form (the `createForm` slot): ONE step — a
  # design <select> (over db.designs) + a name field, then Save. Save runs SetTable's `onCreate`
  # override → sys.create(design, name), spawning a new instance running that design under that name,
  # served at /apps/<name>. The instance is a designer-stored object (db.instances), so the new ROW —
  # including its design column — must appear IN PLACE via the WS refetch (+ resetViewState), with NO
  # reload (the kernel mirror writes the design ref after add-to-set, so the in-place design cell is the
  # load-bearing assertion). Its NEW entry carries the picked design's id, so opening it pre-selects it.
  @milestone-10 @single-user
  Scenario: Creating an instance from the list form spawns it running the picked design
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the instances list
    And I create an instance named "myapp" from the design "crm"
    Then a new instance "myapp" running design "crm" appears in the instances list
    When I open that new instance
    Then the design dropdown has the design "crm" selected

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

  # The create form is client-TOGGLED: revealing it sets the SetTable component's `state.creating = true`
  # and the client re-renders. RefSelect's `foreach c in db.designs` reads data the first paint never
  # shipped (the form was closed) → a value-not-available refetch. The refetch ships the SetTable's whole
  # `state` via slotState — including the NESTED transient `draft` (state.draft = sys.new(desc)) BY VALUE,
  # recursively. The server reconstructs that draft as a throwaway transient, reproduces the open form
  # (RefSelect parent = the real draft, not null), reads `db.designs`, and HARVESTS it — so the picker
  # populates with NO hidden footprint anchor. (The prior build forced db.designs with a `hidden` <ul>
  # foreach over db.designs in instancesListPage; that anchor is now DELETED — this scenario is its
  # replacement guard, proving the nested-draft round-trip alone harvests the candidates.)
  @milestone-10 @single-user
  Scenario: The create-form design picker populates on toggle with no footprint anchor
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the instances list
    And I reveal the instance create form
    Then the instance create form's design ref-select offers the design "todo"
    And the instance create form's design ref-select offers the design "crm"
    When I pick the design "todo" in the create form and name it "anchored" and save
    Then a new instance "anchored" running design "todo" appears in the instances list

  # A prop's cardinality (single / set / dictionary) -- and a dictionary's key type -- are editable in
  # the designer, not just authorable in the .app text. A set's element must be an object type; a
  # dictionary carries a key type. Picking them and applying deploys the collection-shaped props through
  # the same projection the seeds use, so the designer can author the whole data model (sets/dicts), not
  # only single-valued props.
  @milestone-10 @single-user
  Scenario: A prop's cardinality and key type are editable and deploy
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I retype the prop "checked" to "TodoList"
    And I set the prop "checked" cardinality to "set"
    And I set the prop "text" cardinality to "dictionary"
    And I set the prop "text" key type to "text"
    When I open the instance "todo"
    And I apply the design
    Then the "todo" instance's app document declares "checked set of TodoList"
    And the "todo" instance's app document declares "text dict of text by text"

  # An enum type's value list is authorable in the designer, not just in the .app text: adding a type,
  # setting its base type to "enum", and filling its (always-rendered, comma-separated) values input
  # captures the enum in the designer's own store. This stays LIGHT — it asserts the designer captured
  # the authoring, not a full deploy. The two halves the old end-to-end conflated are each tested
  # cheaply on their own: projection rides the host-action publish/apply path, and this scenario only
  # proves the editor captures the authoring. So there is no apply / kernel deploy / 45s file-poll here.
  @milestone-10 @single-user
  Scenario: The designer authors an enum type's base and values
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I add a type to the design
    And I name the just-added type "Status"
    And I set the just-added type's base type to "enum"
    And I set the just-added type's values to "open, doing, done"
    Then the design's type "Status" is an enum with values "open, doing, done"

  # A single `text` prop's `multiline` presentation flag (the generic-UI <textarea> toggle, commit
  # 678eb6d) is authorable in the designer, not just in the .app text. The prop-row has a checkbox bound
  # to prop.multiline, shown ONLY for a single text prop (the one shape the loader allows it on) — so the
  # designer can never produce an invalid design. Toggling it captures the flag in the designer's own
  # store. LIGHT, like the enum authoring above: it asserts the designer captured the toggle + the
  # progressive disclosure (shown on a text prop, hidden on a non-text one), not a full deploy.
  @milestone-multiline @single-user
  Scenario: The designer toggles multiline on a single text prop, and hides it on a non-text prop
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    Then the prop "text" shows a multiline toggle
    And the prop "checked" shows no multiline toggle
    When I toggle multiline on the prop "text"
    Then the design's prop "text" is multiline
    And the design's prop "checked" is not multiline

  # Removing a type from a design must actually delete it. The remove drives arrayRemove on the design's
  # (nested) types set, which runs the store's garbage collector -- and the GC walks the whole meta-schema
  # graph, including a MetaProp object whose `fields` carries a key literally named "type" (the prop's data
  # type) whose value is a tagged-value object. Regression: the GC read that as a scalar tag and threw "must
  # be of type 'JsonValue'" on EVERY nested-set remove, so the server rejected it and the client rolled the
  # row back -- no type (or prop, or design) could be deleted in the designer. The user-reported symptom was
  # "I can't remove a type I just added and haven't named", but it is name- and timing-independent.
  @milestone-10 @single-user
  Scenario: A type added to a design can be removed again
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I add a type to the design
    And I remove the just-added unnamed type
    Then the design "todo" has no unnamed type

  # Progressive disclosure: a field that is only meaningful in one shape is hidden until that shape is
  # chosen, so the editor is not a wall of permanently-blank inputs. The key-type field appears only for
  # a dictionary prop; a type's props editor shows for the object kind and its enum-values field for the
  # enum kind (never both). The kind is a dropdown sourced from the system vocabulary, not free text.
  # This also proves the conditional fields reconcile on the client when cardinality / kind change.
  @milestone-10 @single-user
  Scenario: Irrelevant fields are hidden until their shape is chosen
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    Then the prop "text" shows no key-type field
    When I set the prop "text" cardinality to "dictionary"
    Then the prop "text" shows a key-type field
    When I add a type to the design
    And I name the just-added type "Status"
    Then the just-added type shows a props editor
    And the just-added type shows no values field
    When I set the just-added type's base type to "enum"
    Then the just-added type shows a values field
    And the just-added type shows no props editor

  # The prop-type picker is a dropdown, not a free-text input: it offers the built-in scalar types AND
  # this design's own types, kept in SEPARATE groups so system vocabulary is not flatly intermixed with
  # user-defined types. Picking a type writes it through the <select> binding exactly as before.
  @milestone-10 @single-user
  Scenario: The prop-type picker offers built-in scalars and the design's own types, grouped
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    Then the prop "text" type picker offers the built-in type "text"
    And the prop "text" type picker offers the design type "TodoList"
    And the prop "text" type picker keeps built-in and design types in separate groups

  # The designs list now uses the GENERIC create — the SetTable's own "New" button is the SINGLE create
  # control (the old bespoke .new-design "Add" box is gone). The list customizes the SetTable's create
  # form (a `createForm` slot) to a clean LABEL-ONLY field, so revealing it shows NO raw ui/common/
  # initialData textareas (the default all-scalars form WOULD render those). So the page shows EXACTLY ONE
  # create affordance, and that affordance does not expose the code sections as raw text boxes.
  @milestone-10 @single-user
  Scenario: The designs list shows exactly one create control and it is the generic New
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    Then the designs list shows the generic SetTable New as its only create control
    And the designs list does not show a bespoke Add box
    When I reveal the generic create form
    Then the create form has a labeled "label" field
    And the create form shows no code-section textareas

  # An action-managed SetTable (one given `rowActions`) does NOT sit under the whole-row click overlay
  # (the stretched a.row-link::after that the data-table path uses). So the per-row Edit link and Delete
  # button are directly clickable — no per-consumer z-index band-aid needed. This proves both render and
  # are hit-testable (a click reaches them, not the overlay).
  @milestone-10 @single-user
  Scenario: The designs-list Edit and Delete are clickable, not under a row overlay
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    Then the design "todo" Edit link receives the click
    And the design "todo" Delete button receives the click

  # Deleting a design destroys a whole app (unrecoverable), so Delete is a deliberate two-step inline
  # confirm — the SAME pattern the instances list uses for inline rename. Clicking Delete does NOT remove
  # the row; it arms a confirm (Delete? [Yes] [Cancel]). Cancel restores the plain Delete; Yes removes
  # the design. This verifies the in-row confirm toggles and reconciles correctly on the client.
  @milestone-10 @single-user
  Scenario: Deleting a design asks for confirmation; Cancel restores, Yes removes
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I add a design named "scratch"
    And I click Delete on the design "scratch"
    Then the design "scratch" shows a delete confirmation
    And the design "scratch" is still listed
    When I cancel the delete of the design "scratch"
    Then the design "scratch" shows no delete confirmation
    And the design "scratch" is still listed
    When I click Delete on the design "scratch"
    And I confirm the delete of the design "scratch"
    Then the designs list eventually drops the design "scratch"

  # The IDE nav marks the current section (Instances / Designs) with an is-active class, so the operator
  # can see where they are. Zero new data — the render already computes the section from the path.
  @milestone-10 @single-user
  Scenario: The nav marks the active section
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    Then the nav "Designs" link is active
    And the nav "Instances" link is not active
    When I open the instances list
    Then the nav "Instances" link is active
    And the nav "Designs" link is not active

  # A bad /designs/<id> (no such design) must not render a blank page under the heading — it shows a
  # "not found" message, matching the model's own not-found discipline, with the Back link still present.
  @milestone-10 @single-user
  Scenario: A non-existent design id shows a not-found message
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open a non-existent design
    Then the design editor shows a not-found message
    And the design editor keeps its Back link

  # The design's label is editable IN the editor: a two-way-bound <input> (was a read-only heading), so a
  # design can be renamed where it is edited. The rename is a journaled scalar autosave to the designer's
  # store, so it survives a fresh server render — reloading the editor shows the new label.
  @milestone-10 @single-user
  Scenario: The editor's design label is an editable input that persists a rename
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "draft1"
    And I edit the design "draft1"
    And I rename the design's label to "renamed1"
    And I reload the design editor
    Then the design editor's label input holds "renamed1"

  # The chosen create path is LABEL-ONLY: the generic create form sets the new design's label, then types
  # are added on the EDIT page (the create form cannot author nested types — a set.add carries only the
  # draft's scalar props over the wire, so a type added during create would be silently dropped on Save).
  # This proves the whole flow: create with a label, open the editor, add a type, and it persists to the
  # designer's store (positive ids — the nested type round-tripped through its own arrayAdd).
  @milestone-10 @single-user
  Scenario: Create sets the label, then the edit page adds a type that persists
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "withtype"
    And I edit the design "withtype"
    And I add a type to the design
    And I name the just-added type "Thing"
    Then the design "withtype" has a stored type named "Thing"

  # Each instances-list row gathers its actions into ONE trailing actions cell behind a "⋯" overflow
  # (kebab) menu — supplied to the generic <SetTable> as its `rowActions` cell. The menu is a per-row
  # REACTIVE component: hidden until the row's kebab is clicked, its open/closed state keyed to that
  # row's instance identity, so opening one row's menu does NOT open another's (independent state that
  # survives re-render). The list kebab offers Open / Clone / Delete. RENAME is NOT in the list kebab:
  # <SetTable> owns the identity (name) cell, so an inline in-row rename (which swaps that cell to an
  # input) cannot be driven from a rowActions cell — rename lives on the per-instance detail page
  # (reached via Open), which keeps its own inline rename. (See the detail-page scenario below.)
  @milestone-10 @single-user
  Scenario: Row actions are consolidated into a per-row kebab menu with independent state
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the instances list
    Then the instance "todo" row actions are hidden behind a kebab
    When I open the actions menu for instance "todo"
    Then the instance "todo" actions menu shows Open, Clone, and Delete
    And the instance "crm" actions menu stays closed

  # The instance DETAIL page (/instances/<id>) carries the same kebab, but it must NOT offer "Open" -
  # that would point at the page you are already on (self-referential). So the detail kebab drops Open
  # and keeps Rename/Clone/Delete; choosing Rename opens the SAME inline rename editor in the page head
  # (the established conditional), proving the menu drives the real operation here too.
  @milestone-10 @single-user
  Scenario: The instance detail page kebab omits Open and its Rename opens the inline editor
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the instances list
    And I open the instance "todo"
    And I open the actions menu on the instance page
    Then the instance page actions menu has no Open item
    When I choose Rename from the instance page kebab
    Then the instance page shows the inline rename editor

  # The design-host's `db.instances` is seeded from the kernel registry on EVERY boot (the INVERSE of
  # `db.designs`: both are rebuilt from the live specs). One stored Instance per hosted instance, with
  # `name` = the registry label, `runtimeId` = the kernel id (the link to the runtime row), and `design`
  # = a reference to the matching Design in `db.designs` (resolves by construction — designId is the key).
  # This is a STORE read, not a UI assertion — UI is unchanged in this slice; Slice 3 does the UI change.
  @milestone-10 @single-user
  Scenario: The design-host seeds db.instances from the kernel registry on boot
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    Then the design-host has a stored Instance for each hosted instance
    And the stored Instance for "todo" has runtimeId matching the kernel
    And the stored Instance for "todo" has its design resolved to the "todo" design
    And the stored Instance for "crm" has runtimeId matching the kernel
    And the stored Instance for "crm" has its design resolved to the "crm" design

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

  @milestone-10 @single-user
  Scenario: Deleting an instance via a host action removes its row from db.instances
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When the "crm" instance is deleted via host action
    Then the design-host has no stored Instance named "crm"

  @milestone-10 @single-user
  Scenario: Renaming an instance via a host action updates its name in db.instances
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When the "todo" instance is renamed to "mytodo" via host action
    Then the design-host has a stored Instance named "mytodo"
    And the design-host has no stored Instance named "todo"

  # Slice 3 — the instances list and selector now read from db.instances (the store), not sys.instances
  # (the live kernel set). This scenario verifies that after a host-action rename (which updates
  # db.instances via Slice 2), the instances list page reflects the new name from the STORED data.
  # The step reloads the page so the SSR runs fresh from db.instances (the live kernel also has the
  # new name, so both paths would show it — the key proof is that the SSR uses db.instances fields:
  # inst.name for the label and inst.design.label for the design column).
  @milestone-10 @single-user
  Scenario: The instances list shows renamed instance from db.instances after rename
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When the "todo" instance is renamed to "myrenamedtodo" via host action
    And I open the instances list
    Then the instances list shows the instance "myrenamedtodo" running design "todo"

  # ── the Commit-button UX slice (M13 versioning's last piece) ─────────────────────────────
  #
  # sys.commitDesign(design, message, migration) is now wired lockstep into the AST scan / validator / both
  # interpreters (mirroring sys.publish's existing wiring exactly), and the design editor grows its
  # first versioning surface: a message input + a Commit button + a "Last commit:" confirmation line
  # (DesignCommit.feature is the full spec of the commit mechanism; this scenario is the UI's
  # end-to-end proof it is reachable from the editor, not a re-test of the mechanism itself).
  #
  # UX REVIEW FIX: the message input does NOT clear on click (a synchronous clear both faked "done"
  # before the server ack and destroyed the typed message on a rejected commit). Instead, the positive
  # confirmation is the "Last commit:" line — pure Code reading the design's main branch head — which
  # updates to the just-committed message once the success ack's refetch lands (ws.ts:947). The input
  # is left holding what was typed (retained by construction — nothing clears it either way).
  @milestone-13 @single-user
  Scenario: Committing a design from the editor shows the new commit as the confirmation line
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "first snapshot" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "first snapshot"
    When I open the commit history
    Then the commit history shows a commit with message "first snapshot"

  # The failure leg: committing an invalid design rejects with the global error banner, and the typed
  # message is STILL in the input for retry — proven by construction (nothing ever clears it), not by
  # a special-cased recovery path.
  @milestone-13 @single-user
  Scenario: Committing an invalid design shows the global error banner and keeps the typed message
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I rename the type "TodoItem" to ""
    And I type "won't land" into the commit message
    And I click Commit
    Then the global error banner is shown mentioning "unknown type"
    And the commit message input still holds "won't land"

  # The AST wiring guard: an app whose Code calls sys.commitDesign is detected by
  # HostActionScan.UsesHostActions exactly like the existing sys.publish/sys.delete detection
  # (Kernel.feature's "designer-shaped, uses host actions" scenario) — the wiring this slice adds, proven
  # at the same seam. A real seam is built (the AST wiring works), but the app declares no `sys` rule, so
  # the authority gate still rejects — the same shape-≠-authority proof Kernel.feature already makes for
  # sys.delete, now repeated for sys.commitDesign.
  @milestone-13 @single-user
  Scenario: An app whose Code calls sys.commitDesign is wired for host actions, and the sys rule still gates it
    Given a registry whose only instance is designer-shaped, calls sys.commitDesign, and has no sys rule
    And the kernel has started
    When I send a hostAction "commitDesign" for that instance's own id over its WebSocket
    Then the host action reply over the WebSocket is an error
    And the kernel still hosts that instance

  # Validator arity guard, mirroring sys.publish's existing 2-argument fixed arity: calling
  # sys.commitDesign with the wrong number of arguments fails to LOAD (a load-time schema error), not at
  # first paint — the same class Schema.feature's "loading is rejected" scenarios pin for other builtins.
  @milestone-13 @single-user
  Scenario: sys.commitDesign with the wrong number of arguments fails to load
    Given the app description:
      """
      types
          Db
              designs set of Design
          Design
              label text

      ui
          fn render()
              return <button onClick={() => sys.commitDesign(1)}>
                  "Commit"
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "commitDesign"

  # Empty-message commit: the designer's OTHER inputs (design label, rename) accept and persist an empty
  # value with no client-side guard — a commit message is the same, honest kind of free text, so an
  # empty message is ALLOWED, not silently blocked. Pinned so a future "helpfully" added required-field
  # guard is a deliberate decision, not an accident.
  @milestone-13 @single-user
  Scenario: Committing with an empty message is allowed, matching the editor's other free-text inputs
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I click Commit
    Then the last-commit line eventually shows "(no message)"
    When I open the commit history
    Then the commit history shows a commit with an empty message

  # UX REVIEW FIX 2: the history is newest-first (orderBy descending on logSeq, the honest total
  # order) — a daily glance finds the latest commit on top instead of buried under the boot-time
  # Adopted baselines.
  @milestone-13 @single-user
  Scenario: The commit history lists newest commits first
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "newest one" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "newest one"
    When I open the commit history
    Then the commit history's first row has message "newest one"

  # Review fix 5 — the textarea→commitDesign→detail round-trip has no rendered-UI proof and the
  # binding is load-bearing: open the Migration disclosure, type a valid migration, commit, then
  # confirm it rendered on the commit's detail page.
  @milestone-13 @single-user
  Scenario: Committing a migration from the editor renders it on the commit's detail page
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "with migration" into the commit message
    And I expand the Migration disclosure
    And I type a migration for "TodoItem" into the migration textarea
    And I click Commit
    Then the last-commit line eventually shows message "with migration"
    When I open the commit history
    And I open the commit "with migration" from the history
    Then the commit detail page shows the migration source for "TodoItem"

  # ── Host-action success callback (docs/plans/host-action-success-signal.md) — the commit bar's
  # first consumer. sys.commitDesign's optional trailing fn arg runs ONLY on the ok reply, so a
  # successful commit clears BOTH the message and migration inputs (a committed message is done —
  # retaining it invites a stale re-commit); a rejected commit leaves both exactly as typed (the
  # callback never ran), matching the existing "keeps the typed message" proof but now over BOTH
  # inputs and asserting the CLEAR on the success leg the earlier UX-fix scenario deliberately left
  # unasserted (it predates the callback mechanism — the input was never cleared client-side at all).
  @milestone-13 @single-user
  Scenario: Committing successfully clears the message and migration inputs
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "cleared on success" into the commit message
    And I expand the Migration disclosure
    And I type a migration for "TodoItem" into the migration textarea
    And I click Commit
    Then the last-commit line eventually shows message "cleared on success"
    And the commit message input eventually holds ""
    And the migration textarea eventually holds ""

  # The rejection leg: an invalid migration (naming a type absent from the design — the same shape
  # DesignCommit.feature's "must name a committed type" scenario reproduces server-side) rejects with
  # the global error banner, and the callback never having run means BOTH inputs retain exactly what
  # was typed.
  @milestone-13 @single-user
  Scenario: Committing with an invalid migration keeps both inputs on rejection
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "should not clear" into the commit message
    And I expand the Migration disclosure
    And I type a migration for "Bogus" into the migration textarea
    And I click Commit
    Then the global error banner is shown mentioning "Bogus"
    And the commit message input still holds "should not clear"
    And the migration textarea still holds the migration for "Bogus"

  # Per-design isolation (M13 review fix, D3): commitMessage/commitMigration moved from top-scope ui
  # vars into designEditor's OWN component state, keyed on the design's id (key={sys.id(design)}) — so
  # design A and design B get DIFFERENT slots. Typing a migration under "todo" must not bleed into
  # "crm"'s editor — proven by navigating away and back within ONE tab, no reload. The state move is a
  # REMOUNT on navigation (a fresh slot has fresh state, by construction), so "todo"'s own textarea is
  # ALSO expected empty on return — retention-across-navigation was never promised; only cross-design
  # bleed is what this guards against.
  @milestone-13 @single-user
  Scenario: Typing a migration in one design's editor does not bleed into another design's editor
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I expand the Migration disclosure
    And I type a migration for "TodoItem" into the migration textarea
    And I open the designs list
    And I edit the design "crm"
    And I expand the Migration disclosure
    Then the migration textarea eventually holds ""
    When I open the designs list
    And I edit the design "todo"
    And I expand the Migration disclosure
    Then the migration textarea eventually holds ""

  # B1 — the commit-detail page (/commits/<id>). The history table is LINKED again; clicking a row
  # navigates client-side to the detail page, which resolves the commit by route id and shows its fields
  # (message/at/design/parent/logSeq) + the cached canonical snapshot text, read-only. Back returns to
  # the history list. (Replaces the old "no dead self-link" scenario, which pinned linked={false} while
  # no detail page existed.)
  @milestone-13 @single-user
  Scenario: A commit history row opens the commit-detail page and Back returns
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "snapshot A" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "snapshot A"
    When I open the commit history
    And I open the commit "snapshot A" from the history
    Then the commit detail page shows message "snapshot A"
    And the commit detail page shows design "todo"
    When I navigate back
    Then the commit history shows a commit with message "snapshot A"

  @milestone-13 @single-user
  Scenario: A commit made by a logged-in operator shows its author on the detail page
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "authored snapshot" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "authored snapshot"
    When I open the commit history
    And I open the commit "authored snapshot" from the history
    Then the commit detail page shows author "admin"

  # B1 ride-along: with the history LINKED again, an empty-message commit would otherwise render an empty,
  # unclickable <a> (the phantom the old linked={false} avoided). The generic SetTable now renders a
  # "(no <humanized labelProp>)" placeholder for an empty label WHEN linked, so the row has visible,
  # clickable text that still routes to the detail page. The placeholder humanizes the prop name (matching
  # the library's own convention, e.g. the "Message" column header), so the cell reads "(no Message)" —
  # distinct from the design-editor's page-local last-commit line, which stays the lowercase "(no message)".
  @milestone-13 @single-user
  Scenario: An empty-message commit history row shows a placeholder label and still links
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I click Commit
    Then the last-commit line eventually shows "(no message)"
    When I open the commit history
    Then the commit history's first row link reads "(no Message)"

  # B2 — the "Changes since parent" diff on the commit-detail page. Commit a baseline, then rename a type
  # (retyping the referencing prop so the design stays valid) and commit again. Opening the second commit's
  # detail page shows the STRUCTURAL diff against its parent, computed server-side by sys.diffCommits and
  # shipped via the memo cache (like sys.schema/sys.canRead — no host action, no conformance). The payoff of
  # the identity diff: the type change renders as a RENAME ("TodoItem → Task"), never as a remove+add.
  @milestone-13 @single-user
  Scenario: The commit-detail page shows a rename as a rename in "Changes since parent"
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "baseline" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "baseline"
    When I rename the type "TodoItem" to "Task"
    And I retype the prop "items" to "Task"
    And I type "rename TodoItem" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "rename TodoItem"
    When I open the commit history
    And I open the commit "rename TodoItem" from the history
    Then the changes-since-parent shows a rename from "TodoItem" to "Task"
    And the changes-since-parent shows no removal of "TodoItem"
    When I navigate back
    And I navigate back
    And I add a type to the design
    And I name the just-added type "Project"
    And I add a field "title" to the type "Project"
    And I type "add Project" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "add Project"
    When I open the commit detail for "add Project"
    Then the changes-since-parent shows an add of "Project"

  # ── B3 — Publish + dry-run from the designer ─────────────────────────────────────────────────
  #
  # The design editor grows a Publish section: for each instance running this design, a toggle-gated
  # Preview (the dry-run PublishReport, computed server-side by sys.publishPreview — a server-backed READ
  # shipped via the memo cache like sys.diffCommits, NOT a host action, changing NOTHING) then an Apply
  # (sys.publish — the existing host action). The preview reaches the TARGET's data file read-only; the
  # apply carries data through renames. Three proofs: the dry-run is loud + inert, the apply drives the
  # real publish, and a rename carries data (leaning on Publish.feature's migration-engine proof).

  # 1) The dry-run surfaces a destructive change LOUDLY and changes NOTHING. Remove a leaf field
  # (TodoItem.checked) in the designer and commit, so the design's head diverges from the target's stamped
  # boot baseline by a REMOVAL. Previewing the "todo" instance shows the removal in a destructive (red)
  # class, and the target instance's stored schema STILL has the field — the preview wrote nothing.
  @milestone-13 @single-user
  Scenario: Previewing a publish surfaces a destructive change loudly and changes nothing
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I remove the field "checked" from the type "TodoItem"
    And I type "drop checked" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "drop checked"
    When I preview the publish for the instance "todo"
    Then the publish preview flags "TodoItem.checked" as removed loudly
    And the "todo" instance's app document still describes the field "checked"

  @milestone-13 @single-user
  Scenario: A drift-only publish preview tells the operator to commit instead of offering Apply
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I rename the type "TodoItem" to "Task"
    And I retype the prop "items" to "Task"
    When I preview the publish for the instance "todo"
    Then the publish preview asks me to commit before publishing
    And the publish preview for the instance "todo" shows no Apply button

  @milestone-13 @single-user
  Scenario: The advanced editor deploys an authored access section
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I expand the Advanced code disclosure
    And I type this access section:
      """
      access
          TodoItem
              read
      """
    And I type "grant public todo read" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "grant public todo read"
    When I open the instance "todo"
    And I apply the design
    Then the "todo" instance's app document has an access rule for "TodoItem" granting "read"

  # 2) The confirmed Apply drives the real publish. After a rename+commit, previewing shows the rename;
  # applying fires sys.publish (which stamps the target to the new head), and the target's app document then
  # carries the rename. Re-previewing reads "up to date" — the success signal the operator sees (the diff is
  # now empty). NB the host-action ack runs resetViewState, which closes the open preview toggle; the
  # operator re-opens Preview to confirm — so this step re-previews rather than expecting an auto-refresh.
  @milestone-13 @single-user
  Scenario: Applying a previewed publish deploys the design to the instance
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I rename the type "TodoItem" to "Task"
    And I retype the prop "items" to "Task"
    And I type "rename for apply" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "rename for apply"
    When I preview the publish for the instance "todo"
    Then the publish preview shows a rename from "TodoItem" to "Task"
    When I apply the publish for the instance "todo"
    Then the "todo" instance's app document describes the type "Task"
    And the publish row for instance "todo" eventually shows "Published to todo"
    When I preview the publish for the instance "todo"
    Then the publish preview for the instance "todo" reads up to date

  # 3) A rename carries the target's DATA through the publish. The designer's Publish UI reaches the real
  # rename-safe publish (Publish.feature is the exhaustive proof of the migration engine itself — this proves
  # the UI drives it and the data survives, not a re-test of slice-4). Seed a TodoItem in the target, rename
  # the type in the designer + commit, apply via the UI, then read the target's carried-over data back.
  @milestone-13 @single-user
  Scenario: Applying a rename through the Publish UI carries the target's data
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    And the "todo" target holds a TodoItem with text "buy milk"
    When I open the designs list
    And I edit the design "todo"
    And I rename the type "TodoItem" to "Task"
    And I retype the prop "items" to "Task"
    And I type "rename carries data" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "rename carries data"
    When I preview the publish for the instance "todo"
    And I apply the publish for the instance "todo"
    Then the "todo" instance eventually holds a "Task" with text "buy milk"

  # 4) The preview→apply CONSISTENCY GUARD (addendum). Splitting preview from apply opens a TOCTOU window:
  # the operator approves a SPECIFIC plan (the preview), but an unguarded apply recomputes fresh and could
  # execute a DIFFERENT plan if the target moved in between. The Apply button always passes back the token
  # `sys.publishPreview` handed it (targetCommit + targetVersion); the server rejects a stale apply BEFORE
  # any write. Here the target's OWN data moves (a direct field write bumping its store version) after the
  # preview was taken but before Apply is clicked — the target is never actually published.
  #
  # The target holds a REAL TodoItem (review fix): a rename with NOTHING to migrate (an empty TodoItem
  # extent) never touches the boundary-apply's write path at all (ApplyPublishBoundary short-circuits when
  # there is no data of the affected type), so a stale-reject proof needs actual data at risk of being
  # migrated for the "no write happened" assertion to mean anything.
  @milestone-13 @single-user
  Scenario: Applying a stale preview is rejected and the target is not published
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    And the "todo" target holds a TodoItem with text "must not be migrated"
    When I open the designs list
    And I edit the design "todo"
    And I rename the type "TodoItem" to "Task"
    And I retype the prop "items" to "Task"
    And I type "rename then stale apply" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "rename then stale apply"
    When I preview the publish for the instance "todo"
    Then the publish preview shows a rename from "TodoItem" to "Task"
    And the "todo" target's data changes since the preview
    When I apply the publish for the instance "todo"
    Then the global error banner is shown mentioning "changed since the preview"
    And the "todo" instance's app document does not describe the type "Task"
    And the "todo" target's data is unchanged by the rejected apply

  # 5) The guard's OTHER leg: a CLEAN (non-stale) guarded apply on the VERSIONED path still succeeds
  # end-to-end and carries data — proving the guard rejects ONLY a genuinely stale token, never a fresh one.
  # (Scenario 3 above already proves the UI-driven rename+data-carry; this is the same shape but explicitly
  # on the guarded 4-arg sys.publish call, since scenario 3 predates the addendum and never exercised it.)
  @milestone-13 @single-user
  Scenario: A clean guarded apply on the versioned path still succeeds and carries data
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    And the "todo" target holds a TodoItem with text "guarded apply keeps me"
    When I open the designs list
    And I edit the design "todo"
    And I rename the type "TodoItem" to "Task"
    And I retype the prop "items" to "Task"
    And I type "clean guarded apply" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "clean guarded apply"
    When I preview the publish for the instance "todo"
    Then the publish preview shows a rename from "TodoItem" to "Task"
    When I apply the publish for the instance "todo"
    Then the "todo" instance's app document describes the type "Task"
    And the "todo" instance eventually holds a "Task" with text "guarded apply keeps me"

  # ── B4 — Branch UI + createBranch/mergeBranch from the designer ───────────────────────────────
  #
  # The design editor grows a Branches section: create a branch (sys.createBranch — a host action), see
  # the design's branches as links to their own /designs/<id> editors (a branch working copy is a Design
  # row at its own URL — switching branches is navigation), and merge a branch back in via a toggle-gated
  # sys.mergePreview (a server-backed READ shipped via the memo cache like sys.publishPreview, NOT a host
  # action, changing NOTHING) then an Apply (sys.mergeBranch — the host action). The merge machinery itself
  # (three-way merge, conflicts, resolutions, access-change surfacing) is exhaustively proven at the WS-op
  # level in DesignMerge.feature; these scenarios prove the UI drives it end-to-end.

  # 1) Create a branch: commit a baseline (so the branch has a head to clone), then create a branch named
  # "feature". It appears in the Branches section as a branch link (a Design row at its own URL).
  @milestone-13 @single-user
  Scenario: Creating a branch from the editor lists it as a branch link
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "baseline for branching" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "baseline for branching"
    When I create a branch named "feature"
    Then the Branches section lists a branch link "feature"

  # 2) A clean merge carries a disjoint change. Commit a baseline, branch, add a field on the branch and
  # commit it there, then merge the branch back into the main design — a clean merge (disjoint edit), and
  # the main design's type now carries the branch's new field. Proves the whole preview→apply UI path.
  @milestone-13 @single-user
  Scenario: Merging a branch cleanly carries its change into the target design
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "baseline for merge" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "baseline for merge"
    When I create a branch named "feature"
    And I open the branch "feature" from the Branches section
    And I add a field "priority" to the type "TodoItem"
    And I type "add priority on branch" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "add priority on branch"
    When I open the designs list
    And I edit the design "todo"
    And I preview the merge of branch "feature"
    Then the merge preview reports a clean merge
    When I apply the merge of branch "feature"
    Then the Branches section eventually shows "Merged feature into this design"
    And the design "todo" eventually has a stored prop named "priority" on "TodoItem"
    And the merge preview reports already up to date

  # 3) A conflict is shown, resolved by a per-conflict pick, then applied. Rename the same prop differently
  # on the branch and on main; the merge preview surfaces the conflict with its base/source/target values,
  # Apply stays gated until the conflict is resolved, and picking a side unlocks the merge.
  @milestone-13 @single-user
  Scenario: A merge conflict renders, is resolved by a pick, and then merges
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "baseline for conflict" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "baseline for conflict"
    When I create a branch named "feature"
    And I open the branch "feature" from the Branches section
    And I rename the prop "text" to "heading" on the type "TodoItem"
    And I type "rename to heading on branch" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "rename to heading on branch"
    When I open the designs list
    And I edit the design "todo"
    And I rename the prop "text" to "caption" on the type "TodoItem"
    And I type "rename to caption on main" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "rename to caption on main"
    When I preview the merge of branch "feature"
    Then the merge preview shows a conflict with source "heading" and target "caption"
    And the merge preview shows no Merge button
    When I take source for the first conflict
    And I apply the merge of branch "feature"
    Then the design "todo" eventually has a stored prop named "heading" on "TodoItem"

  # 4) The access-change must-see block. A merge that introduces an access-rule difference ALWAYS surfaces
  # it (never silently folded in — the settled security rule), even on an otherwise-clean merge. Grant a
  # read rule on the branch (reusing the slice-5 store-level access mutation), commit it there, then the
  # merge preview on main renders the loud AccessChanges block naming the rule.
  @milestone-13 @single-user
  Scenario: A merge that changes an access rule surfaces the must-see access block
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    And I type "baseline for access" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "baseline for access"
    When I create a branch named "feature"
    And I grant read on "TodoItem" to everyone on the branch "feature"
    And I open the branch "feature" from the Branches section
    And I type "grant read on branch" into the commit message
    And I click Commit
    Then the last-commit line eventually shows message "grant read on branch"
    When I open the designs list
    And I edit the design "todo"
    And I preview the merge of branch "feature"
    Then the merge preview's access block mentions "TodoItem"

  # ── M12 X2b — the "Convert to structured" button + the structured render view ─────────────────
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

  # ── M12 E1 — the structured-render TREE EDITOR: recursive, inline scalar editing ──────────────────
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

  # ── M12 E2 — the structured-render tree editor becomes STRUCTURALLY editable ───────────────────
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

  # ── M12 CANVAS-1 — the CLIENT-COMPUTABLE canvas (sys.renderTree) ──────────────────────────────
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

  # ── M12 CANVAS-EVAL-1 — the canvas EVALUATES expressions (sys.evalContext) ─────────────────────
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

  # ── M12 auto-live parse-op — the canvas evaluates a NEWLY EDITED expression WITHOUT "Refresh values" ──
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

  # ── M12 eval-degrade-banner — an honest notice when evalContext itself fails to build ────────────
  #
  # BuildEvalContext's catch arm (an invalid design — e.g. a root type left at baseType "object" with ZERO
  # props, the legitimate mid-authoring state before the operator adds a field) degrades to an EMPTY
  # payload silently: with no `error` signal, the canvas would just sit chipped/blank with no clue why. This
  # slice makes the degrade carry the REAL exception message and splices ONE div.eval-degrade-banner ahead
  # of the tree — never a paraphrase, never silence. The type card ALSO gets a small inline hint ("needs at
  # least one field") for the same zero-props state, the fnNameHint idiom.
  #
  # A literal render (no `db.` reference — the leaf is a plain string) imports fine with zero types at all,
  # so the ONLY thing making evalContext fail here is the fieldless "Db" type. Adding a field and clicking
  # Refresh must clear the banner (the S3a-race idiom: only an explicit Refresh recomputes evalContext).
  @m12 @single-user
  Scenario: An invalid design (a fieldless root type) shows an honest degrade notice on the canvas, and the type card hints at the cause; fixing it and refreshing clears both
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "brokenme"
    And I edit the design "brokenme"
    When I add a type to the design
    And I name the just-added type "Db"
    Then the just-added type shows the hint "needs at least one field"
    When I ensure the Advanced code disclosure is open
    And I author a literal convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    And the design canvas shows the eval-degrade notice mentioning "Type 'Db' has baseType 'object' but no fields"
    When I add a field "greeting" to the type "Db"
    Then the just-added type shows no hint
    When I click Refresh values
    Then the design canvas does not show the eval-degrade notice
    And the design canvas shows the evaluated leaf text "Hello"

  # ── M12 S6a — `foreach`/`if` become structured ROWS (rows + canvas template mode) ───────────────
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

  # ── M12 S6b — the canvas EVALUATES for/if rows (row-scope evaluation) ────────────────────────────
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

  # ── M12 F1 — structured fns: rows + import + projection + editor ────────────────────────────────
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

  # ── M12 V1 — MetaVar rows: component state + top-level ui vars ───────────────────────────────────
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

  # ── M12 F1 review fix (ui-arch + ux) — the from-scratch "+ Component" flow, no import ────────────
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

  # ── M12 F2 — canvas expansion of design-component invocations ────────────────────────────────
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

  # ── M12 F3 — call-position evaluation of design fns ──────────────────────────────────────────
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

  # ── M12 F3b review fix — an UNNAMED fn (the "+ Component" mid-authoring state) is symmetrically
  # excluded from the staleness comparison ─────────────────────────────────────────────────────
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

  # ── M12 V1b — init-evaluated state in the static canvas ───────────────────────────────────────
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

  # ── M12 U1 — MetaUse rows: the Configurations editor + static per-configuration preview ─────
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

  # ── M12 W1a — the live-instance driver ────────────────────────────────────────────────────────
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
  @m12 @single-user
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

  # ── M12 W1b — events + Reset through the dispatch bracket ────────────────────────────────────────
  #
  # W1a mounted a real running instance but left it INERT (noWiring — the isolation bracket only wrapped
  # RENDER, and a click fires from the DOM long after that bracket restored the page's real globals). W1b
  # adds the dispatch-time bracket every instance event routes through (runInstanceHandler), the matching
  # wiring strategy (instanceWiring), and Reset — a framework-owned control bar the driver renders inside
  # the container. The independence-at-CLICK pin is the arc's headline: two configurations, click one,
  # only it changes.
  @m12 @single-user
  Scenario: An instance's own click handler only affects that instance, and Reset returns it to its initial state
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "wbcounterme"
    And I edit the design "wbcounterme"
    And I add a type to the design
    And I name the just-added type "Db"
    And I add a field "note" to the type "Db"
    When I ensure the Advanced code disclosure is open
    And I author a convertible render with a reactive Counter component into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I click the add-configuration button
    And I click the add-configuration button
    Then component configurations shows 2 rows
    And configuration 0's live instance shows a "button" element reading "0"
    And configuration 1's live instance shows a "button" element reading "0"
    When I click configuration 0's live instance button
    And I click configuration 0's live instance button
    Then configuration 0's live instance shows a "button" element reading "2"
    And configuration 1's live instance shows a "button" element reading "0"
    When I click configuration 0's live instance Reset button
    Then configuration 0's live instance shows a "button" element reading "0"
    And configuration 1's live instance shows a "button" element reading "0"

  # Two-way binding (value= state writes) through the SAME dispatch bracket the click path uses, plus the
  # bracket-restore proof: the page's own editing (a design rename, an admin-gated autosave) still works
  # right after an instance's handler ran. Also carries the ANCHOR-CONTAINMENT pin (arch review fold): the
  # TextBox fixture's own `<a href>` has NO onClick — nothing in instanceWiring stops it — so only the
  # container-level click swallow (workbench.ts ensureInstanceContent) stops it bubbling to the page's
  # document-level interceptNavigation and navigating the whole designer away.
  @m12 @single-user
  Scenario: Typing into an instance's two-way-bound input repaints only that instance, and the page's own editing still works
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "twowayme"
    And I edit the design "twowayme"
    And I add a type to the design
    And I name the just-added type "Db"
    And I add a field "note" to the type "Db"
    When I ensure the Advanced code disclosure is open
    And I author a convertible render with a two-way-bound TextBox component into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I click the add-configuration button
    Then component configurations shows 1 row
    When I type "hello" into configuration 0's live instance input
    Then configuration 0's live instance shows a "span" element reading "hello"
    When I click configuration 0's live instance link
    Then the design editor eventually shows the structured render tree editor
    And configuration 0's live instance shows a "span" element reading "hello"
    When I rename the design's label to "twowayme-renamed"
    Then the design editor shows the design's label "twowayme-renamed"

  # THE SESSION-SAFETY PIN (component-workbench.md's "grill's core fix"): sys.login/sys.logout are NOT
  # id-gated, so a login/logout call inside a sandboxed instance's handler could otherwise really re-bind
  # the operator's own page session. Only the dispatch bracket's wsHooks-null stops it. Chained with a
  # page-side rename (an admin-gated autosave) as a SECOND, stronger proof: if the real session had flipped
  # anonymous, that write would be silently denied and the rename step's own store poll would time out.
  @m12 @single-user
  Scenario: A card's handler calling sys.logout never touches the page's own session
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "logoutme"
    And I edit the design "logoutme"
    And I add a type to the design
    And I name the just-added type "Db"
    And I add a field "note" to the type "Db"
    When I ensure the Advanced code disclosure is open
    And I author a convertible render with a sandboxed logout button component into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I click the add-configuration button
    Then configuration 0's live instance shows a "button" element reading "Log out (sandboxed)"
    When I click configuration 0's live instance button
    Then the designer's own session is still logged in
    When I rename the design's label to "logoutme-renamed"
    Then the design editor shows the design's label "logoutme-renamed"

  # A throwing handler (an unseeded-ambient read, the same v1 fidelity boundary as render time — W1c seeds
  # schema/extent but NOT ambients) renders the REAL error into its own card — never a rollback, never a
  # page-wide crash, and a SIBLING instance (a different component, in this design) stays fully interactive,
  # proving the isolation bracket is per-dispatch, not something one broken handler can wedge for the whole
  # page.
  @m12 @single-user
  Scenario: A throwing instance handler shows the real error without disabling the page or its sibling instance
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "throwme"
    And I edit the design "throwme"
    And I add a type to the design
    And I name the just-added type "Db"
    And I add a field "note" to the type "Db"
    When I ensure the Advanced code disclosure is open
    And I author a convertible render with a throwing component and a Counter component into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I click the add-configuration button for "Thrower"
    And I click the add-configuration button for "Counter"
    Then component configurations shows 2 rows
    And configuration 0's live instance shows a "button" element reading "boom"
    And configuration 1's live instance shows a "button" element reading "0"
    When I click configuration 0's live instance button
    Then configuration 0's live instance shows the error "Variable currentUser not found"
    When I click configuration 1's live instance button
    Then configuration 1's live instance shows a "button" element reading "1"
    When I rename the design's label to "throwme-renamed"
    Then the design editor shows the design's label "throwme-renamed"

  # ── M12 W1c — sandbox cache seeding: schema:/extent:/canWrite:/canRead: + library binding ───────
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

  # The isolation pins (W1a/W1b) EXTENDED to generic UI: typing into a real <Field>'s editor (sys.schema-
  # backed, sys.new-drafted) writes only THIS instance's local draft — a sibling configuration of the SAME
  # component, and the page's own editing, are both untouched.
  @m12 @single-user
  Scenario: Typing into a Field editor inside an instance updates only that instance's local draft
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "seedfieldtype"
    And I edit the design "seedfieldtype"
    And I add a type to the design
    And I name the just-added type "Db"
    And I add a field "note" to the type "Db"
    And I add a type to the design
    And I name the just-added type "Note"
    And I add a field "title" to the type "Note"
    When I ensure the Advanced code disclosure is open
    And I author a convertible render with a stateful schema-backed Editor component into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I click the add-configuration button
    And I click the add-configuration button
    Then component configurations shows 2 rows
    When I type "hello" into configuration 0's live instance input
    Then configuration 0's live instance shows a "span" element reading "hello"
    And configuration 1's live instance shows a "span" element reading ""
    When I rename the design's label to "seedfieldtype-renamed"
    Then the design editor shows the design's label "seedfieldtype-renamed"

  # Reset (the whole-sandbox disposal W1b established) now covers the SEEDED extent too: a handler-added
  # row (db.notes.add via a freshly sys.new-minted draft, schema-backed) shows up in sys.extent immediately
  # (mutation-consistent, per-instance derivation), and Reset discards it along with the rest of the sandbox.
  @m12 @single-user
  Scenario: Reset returns an instance's seeded extent to its initial rows after a handler mutates it
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "seedresetextent"
    And I edit the design "seedresetextent"
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
              notes: [2]
          Note 2
              title: "Alpha"
      """
    When I ensure the Advanced code disclosure is open
    And I author a convertible render with an extent-adding component into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    When I click the add-configuration button
    Then configuration 0's live instance shows 1 "li" element
    When I click configuration 0's live instance button
    Then configuration 0's live instance shows 2 "li" elements
    When I click configuration 0's live instance Reset button
    Then configuration 0's live instance shows 1 "li" element

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
