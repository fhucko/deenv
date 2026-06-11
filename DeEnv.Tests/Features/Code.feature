Feature: Code-owned UI rendering (server-side)
  The Code milestone gives the instance hand-written behaviour and UI as an AST.
  When a `ui` section exists, code owns all routing: the render function is
  executed on the server and its tag tree is serialised to HTML. This covers the
  Stage-2 SSR surface — elements, text, bound fields, foreach, if/else, and the
  where/orderBy collection functions — plus load-time structural validation and
  the generic auto-form fallback when there is no `ui`.

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
    Given the schema document:
      """
      {
        "types": [ { "name": "Db", "baseType": "object",
          "props": [ { "name": "note", "type": "text" } ] } ],
        "ui": {
          "render": { "type": "fn", "params": [], "body": { "type": "block", "statements": [
            { "type": "return", "value":
              { "type": "tag", "name": "div", "attributes": [], "children": [
                { "type": "symbol", "name": "missing" }
              ] } }
          ] } }
        }
      }
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "missing"

  @milestone-code @single-user
  Scenario: A two-way binding to a read-only target is rejected at load
    Given the schema document:
      """
      {
        "types": [
          { "name": "Db", "baseType": "object",
            "props": [ { "name": "things", "type": "Thing", "cardinality": "set" } ] },
          { "name": "Thing", "baseType": "object",
            "props": [ { "name": "name", "type": "text" } ] }
        ],
        "ui": {
          "render": { "type": "fn", "params": [], "body": { "type": "block", "statements": [
            { "type": "return", "value":
              { "type": "tag", "name": "div", "attributes": [], "children": [
                { "type": "foreach", "item": { "name": "t" },
                  "collection": { "type": "infixOp", "op": "objectProp",
                    "left": { "type": "symbol", "name": "db" },
                    "right": { "type": "symbol", "name": "things" } },
                  "body": [
                    { "type": "tag", "name": "input", "attributes": [
                      { "name": "type",  "value": { "type": "text", "value": "text" } },
                      { "name": "value", "value": { "type": "symbol", "name": "t" } }
                    ], "children": [] }
                  ] }
              ] } }
          ] } }
        }
      }
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "read-only"

  @milestone-code @single-user
  Scenario: A runtime error during first paint renders an SSR error page
    # The render adds 1 to a text field — a structurally-valid AST that throws a
    # type error at runtime. The renderer catches it and serves an error page.
    Given the code instance:
      """
      {
        "types": [ { "name": "Db", "baseType": "object",
          "props": [ { "name": "note", "type": "text" } ] } ],
        "ui": {
          "render": { "type": "fn", "params": [], "body": { "type": "block", "statements": [
            { "type": "return", "value":
              { "type": "tag", "name": "div", "attributes": [], "children": [
                { "type": "infixOp", "op": "add",
                  "left": { "type": "infixOp", "op": "objectProp",
                    "left": { "type": "symbol", "name": "db" },
                    "right": { "type": "symbol", "name": "note" } },
                  "right": { "type": "int", "value": 1 } }
              ] } }
          ] } }
        }
      }
      """
    When the page at "/" is rendered
    Then the rendered HTML contains "<h1>Error</h1>"

  @milestone-code @single-user
  Scenario: The generic auto-form is still used when there is no ui section
    Given a generic instance with no code
    When the page at "/" is rendered
    Then the rendered HTML contains 'id="node-form"'

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

