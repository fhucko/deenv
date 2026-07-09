Feature: Code-owned UI rendering (server-side)
  The Code milestone gives the instance hand-written behaviour and UI as an AST.
  When a `ui` section exists, code owns all routing: the render function is
  executed on the server and its tag tree is serialised to HTML. This covers the
  Stage-2 SSR surface — elements, text, bound fields, foreach, if/else, and the
  where/orderBy collection functions — plus load-time structural validation and
  the self-hosted generic UI that serves apps without a `fn render()`.

  @milestone-code @single-user
  Scenario: An element with interpolated text renders
    Given the tasks UI instance seeded with sample tasks
    When the page at "/" is rendered
    Then the rendered HTML contains "<h1>Tasks</h1>"

  @milestone-code @single-user
  Scenario: A bound text field renders its value
    Given the tasks UI instance seeded with sample tasks
    When the page at "/" is rendered
    Then the rendered HTML contains 'value="Alpha"'

  @milestone-code @single-user
  Scenario: A bound checkbox is checked only for the done task
    Given the tasks UI instance seeded with sample tasks
    When the page at "/" is rendered
    Then the rendered HTML contains "checked" exactly 1 times

  @milestone-code @single-user
  Scenario: A foreach list is ordered by the orderBy key
    Given the tasks UI instance seeded with sample tasks
    When the page at "/" is rendered
    Then the rendered HTML contains "Alpha" before "Beta"
    And the rendered HTML contains "Beta" before "Gamma"

  @milestone-code @single-user
  Scenario: An if/else inside foreach selects per-item branches
    Given the tasks UI instance seeded with sample tasks
    When the page at "/" is rendered
    Then the rendered HTML contains ">done<"
    And the rendered HTML contains ">open<"

  @milestone-code @single-user
  Scenario: A where clause filters the done task out of the open list
    Given the tasks UI instance seeded with sample tasks
    When the page at "/" is rendered
    # Alpha (done) appears once, only in the unfiltered list; Beta (open) appears
    # twice — in the unfiltered list and the where-filtered open list.
    Then the rendered HTML contains "Alpha" exactly 1 times
    And the rendered HTML contains "Beta" exactly 2 times

  @milestone-code @single-user
  Scenario: A binding to an undeclared symbol is rejected at load
    Given the app description:
      """
      types
          Db
              note text

      ui
          fn render()
              return <div>
                  missing
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "missing"

  @milestone-code @single-user
  Scenario: A two-way binding to a read-only target is rejected at load
    Given the app description:
      """
      types
          Db
              things set of Thing
          Thing
              name text

      ui
          fn render()
              return <div>
                  foreach t in db.things
                      <input type="text" value={t}>
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "read-only"

  @milestone-code @single-user
  Scenario: A syntax error in the code is rejected at load with its position
    Given the app description:
      """
      types
          Db
              note text

      ui
          fn render()
              return <div>
                  oops =
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "line 8"

  @milestone-code @single-user
  Scenario: A runtime error during first paint renders an SSR error page
    # The render takes the intrinsic id of a TEXT field — structurally-valid code that
    # throws a type error at runtime (sys.id expects an object). The renderer catches it
    # and serves an error page. (`db.note + 1` no longer errors: `+` now concatenates a
    # string with an int, so a genuinely type-erroring call is used here instead.)
    Given the code instance:
      """
      types
          Db
              note text

      ui
          fn render()
              return <div>
                  sys.id(db.note)
      """
    When the page at "/" is rendered
    Then the rendered HTML contains "<h1>Error</h1>"

  @m12 @single-user
  Scenario: A runaway-recursive helper hits the call-depth guard and renders an SSR error page, not a crash
    # Grill F3c (M12 FG): a self-calling helper with no base case would recurse forever pre-guard —
    # an UNCATCHABLE StackOverflowException on the C# twin (process death), the exact blast radius
    # that would let designer DATA crash the designer's own render in a loop once F3 evaluates helper
    # calls during the canvas walk. The call-depth guard turns unbounded recursion into a normal,
    # catchable CodeRuntimeException — caught by the SAME top-level SSR catch as any other runtime
    # error (the "runtime error during first paint" scenario above), not new SSR error chrome.
    Given the code instance:
      """
      types
          Db
              note text

      ui
          fn render()
              return <div>
                  Rec()
          fn Rec()
              return Rec()
      """
    When the page at "/" is rendered
    Then the rendered HTML contains "<h1>Error</h1>"
    And the rendered HTML contains "Call depth exceeded 256"

  @milestone-code @single-user
  Scenario: The self-hosted generic UI serves an app with no fn render()
    Given a generic instance with no code
    When the page at "/" is rendered
    Then the rendered HTML contains "object-form"
    And the page does not include 'id="node-form"'

  @milestone-code @single-user
  Scenario: A sensitive field and unauthorized rows are never sent to the client
    # `salary` is sensitive; `highEarners` (server-only) filters by it and ships only
    # its result. The client gets the high earner's name, but no salary value and no
    # non-earner row — db.people is read only on the server.
    Given the people instance seeded with salaries
    When the page at "/" is rendered
    Then the rendered HTML contains "Ada"
    And the page does not include "999"
    And the page does not include "Bob"

  # The privacy invariant when a filtered collection is nested inside a MINTED object that ships:
  # `var box = { rows: db.people.where(p => p.salary > 100) }` is a top-scope var, so it ships; the
  # render DISPLAYS only the > 600 subset (Ada), so Cleo (matches the box filter, salary 500) is
  # never displayed. The minted `box` must ship `rows` ACCESS-SCOPED — only the displayed Ada — never
  # Cleo's membership. Pins the ship-whole boundary: only a provably-constant sys.schema descriptor
  # ships its full interior; a minted object wrapping a where-result stays access-scoped. (Rows are
  # positive-id db objects, so no field VALUE of an undisplayed row ever ships; the broad "ship any
  # negative-id array nested in a complete object" rule leaked the undisplayed rows' MEMBERSHIP — the
  # array item + an empty object stub — which the client-state count assertion below pins.)
  @milestone-11 @single-user
  Scenario: A filtered collection nested in a minted object never spills its undisplayed rows
    Given the nested-filter privacy instance seeded with salaries
    When the page at "/" is rendered
    Then the rendered HTML contains "Ada"
    And the page does not include "Cleo"
    And the minted collection ships only its displayed rows

  # A <select value={x}> marks the <option> whose value equals x as `selected` — and `value`
  # is NOT emitted on the <select> itself (it is not real HTML there; it drives the selection).
  # The bound value is a NON-first option (the 2nd), so this proves the SSR selection is driven
  # by the value, not by the browser's default-first behaviour.
  @milestone-code @single-user
  Scenario: A select binds the matching option as selected on first paint
    Given the code instance:
      """
      types
          Db
              ready bool

      ui
          var chosen = "g"

          fn render()
              return <select class="pick" value={chosen}>
                  <option value="r">
                      "Red"
                  <option value="g">
                      "Green"
                  <option value="b">
                      "Blue"
      """
    When the page at "/" is rendered
    Then the rendered HTML contains '<option value="g" selected>'
    And the rendered HTML does not contain '<option value="r" selected>'
    And the rendered HTML does not contain '<select class="pick" value='

  # Two XSS sinks at the attribute-emit chokepoint (AppendCodeAttribute): a `javascript:`-scheme
  # href/src is a clickable script URL even though HtmlEncode has no special characters to escape
  # there, and a STRING value on an `on*` attribute renders an inline handler no legitimate binding
  # would ever produce (real handlers are `fn` values, wired client-side, never scalars here). Both
  # values are seeded via initialData so they flow through the real Code→attribute path, not a
  # template literal — proving the guard fires on real data, not just a hardcoded string.
  @milestone-code @single-user
  Scenario: A javascript: href is neutralized
    Given the code instance:
      """
      types
          Db
              link text
              safeLink text

      initialData
          Db 1
              link: "javascript:alert(1)"
              safeLink: "/notes/2"

      ui
          fn render()
              return <div>
                  <a href={db.link}>
                      "Click me"
                  <a href={db.safeLink}>
                      "Safe link"
      """
    When the page at "/" is rendered
    Then the rendered HTML does not contain "javascript:alert(1)"
    And the rendered HTML contains '<a href="/notes/2">Safe link</a>'
    And the rendered HTML contains '<a>Click me</a>'

  @milestone-code @single-user
  Scenario: A string onclick attribute is dropped
    Given the code instance:
      """
      types
          Db
              evil text

      initialData
          Db 1
              evil: "alert(1)"

      ui
          fn render()
              return <div onclick={db.evil}>
                  "hi"
      """
    When the page at "/" is rendered
    Then the rendered HTML does not contain "onclick"

  # The client-twin proof (refreshAttributes in ui.ts): the SSR scenario above proves the string
  # never leaves the server; this proves the HYDRATED DOM (post client-side reconcile) never carries
  # it either — a real browser, not just the C# SSR string, so a guard applied only on the SSR edge
  # and silently missing on the client twin would still show green on the SSR scenario but fail here.
  @milestone-code @single-user
  Scenario: A javascript: href is neutralized in the hydrated DOM
    Given the code instance is served in a browser:
      """
      types
          Db
              link text

      initialData
          Db 1
              link: "javascript:alert(1)"

      ui
          fn render()
              return <a class="evil-link" href={db.link}>
                  "Click me"
      """
    When I open "/"
    Then the element ".evil-link" has no "href" attribute

  @milestone-code @single-user
  Scenario: A string onclick attribute is dropped in the hydrated DOM
    Given the code instance is served in a browser:
      """
      types
          Db
              evil text

      initialData
          Db 1
              evil: "alert(1)"

      ui
          fn render()
              return <div class="evil-div" onclick={db.evil}>
                  "hi"
      """
    When I open "/"
    Then the element ".evil-div" has no "onclick" attribute

