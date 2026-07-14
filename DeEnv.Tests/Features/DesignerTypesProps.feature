Feature: Designer - Types and Props Editor


  # The "todo" design is the REAL todo app: its types include TodoItem and its UI is the real
  # custom `fn render()` — so the editor (now at /designs/<id>) shows that app's actual content.
  @milestone-10 @single-user
  Scenario: Editing a design shows its real types and its UI text
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I edit the design "todo"
    Then the design editor shows a type named "TodoItem"
    And the design editor shows the design's UI text in a textarea


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


  # ──── M12 eval-degrade-banner — an honest notice when evalContext itself fails to build ────────────────────────
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


  # ──── M12 S4b — the page-order reorder: types ΓåÆ render ΓåÆ publish ΓåÆ branches ──────────────────────────────────────────────────────
  #
  # The UX ledger's one high-value reorder (visual-designer.md, 2026-07-08 checkpoint): publish/branches
  # used to split the canvas+tree authoring pair from the type editor above it. A markup move only — the
  # section calls themselves are untouched, just reordered in designEditor.
  @m12 @single-user
  Scenario: The design editor's sections are ordered types, render, publish, branches
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I open the designs list
    And I create a design named "pageorder"
    And I edit the design "pageorder"
    When I ensure the Advanced code disclosure is open
    And I author a selection-test convertible render into the design's UI
    When I click Convert to structured
    Then the design editor eventually shows the structured render tree editor
    Then the design editor's sections are ordered types, render, publish, branches
