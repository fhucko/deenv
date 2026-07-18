Feature: Durable lists — ordered sequences with duplicate refs
  Slice 2+3 of durable lists: `list of T` is a first-class cardinality that
  persists end-to-end (parse/print, mint empty, seed, load, GC edges, thin
  publish reshape) plus unified-commit mutators (listReplace/Insert/RemoveAt/Move).
  No list-level OCC. Generic list table lands in Slice 4.

  # ── schema / document ──────────────────────────────────────────────────────

  @milestone-lists @single-user
  Scenario: list of T parses and the store mints an empty list with a positive id
    Given the app description:
      """
      types
          Db
              tasks list of Task
          Task
              title text
      """
    When the store is opened
    Then the store opens successfully
    And the root list "tasks" is empty with a positive id

  @milestone-lists @single-user @persistence
  Scenario: Seeded list order and duplicate object refs survive a reload
    Given the app description:
      """
      types
          Db
              tasks list of Task
          Task
              title text

      initialData
          Db 1
              tasks: [3, 2, 3]
          Task 2
              title: "B"
          Task 3
              title: "A"
      """
    When the store is opened
    Then the store opens successfully
    And the root list "tasks" has member ids 3, 2, 3 in order
    When the store is opened again on the same data file
    Then the root list "tasks" has member ids 3, 2, 3 in order

  @milestone-lists @single-user @persistence
  Scenario: A list-only reference keeps an object alive across GC
    Given the app description:
      """
      types
          Db
              tasks list of Task
              bag set of Task
          Task
              title text
      """
    And a stored data file containing:
      """
      {
        "extents": {
          "Db": { "1": { "type": "object", "typeName": "Db", "id": 1, "fields": {
            "tasks": { "type": "list", "id": 2, "items": [
              { "type": "object", "typeName": "Task", "id": 5 },
              { "type": "object", "typeName": "Task", "id": 5 }
            ] },
            "bag": { "type": "set", "id": 3, "members": {
              "6": { "type": "object", "typeName": "Task", "id": 6 } } }
          } } },
          "Task": {
            "5": { "type": "object", "typeName": "Task", "id": 5, "fields": {
              "title": { "type": "text", "value": "via-list" } } },
            "6": { "type": "object", "typeName": "Task", "id": 6, "fields": {
              "title": { "type": "text", "value": "via-set" } } },
            "7": { "type": "object", "typeName": "Task", "id": 7, "fields": {
              "title": { "type": "text", "value": "orphan" } } }
          }
        },
        "root": { "type": "object", "typeName": "Db", "id": 1 },
        "nextId": 10
      }
      """
    When the store is opened
    And member 6 is removed from set id 3
    Then the extent "Task" has objects 5 only

  # ── Slice 3 mutators (unified commit, OCC-free) ────────────────────────────

  @milestone-lists @single-user @persistence
  Scenario: listReplace keeps the same list id and order across reload
    Given the app description:
      """
      types
          Db
              tasks list of Task
          Task
              title text

      initialData
          Db 1
              tasks: [3, 2]
          Task 2
              title: "B"
          Task 3
              title: "A"
      """
    When the store is opened
    And the root list "tasks" is replaced with member ids 2, 3, 2
    Then the root list "tasks" has member ids 2, 3, 2 in order
    And the root list "tasks" kept its positive id
    When the store is opened again on the same data file
    Then the root list "tasks" has member ids 2, 3, 2 in order
    And the root list "tasks" kept its positive id

  @milestone-lists @single-user
  Scenario: listInsert, listMove, listRemoveAt mutate order
    Given the app description:
      """
      types
          Db
              tasks list of Task
          Task
              title text

      initialData
          Db 1
              tasks: [3, 2]
          Task 2
              title: "B"
          Task 3
              title: "A"
      """
    When the store is opened
    And member 2 is inserted at index 0 of root list "tasks"
    Then the root list "tasks" has member ids 2, 3, 2 in order
    When the root list "tasks" moves index 0 to 2
    Then the root list "tasks" has member ids 3, 2, 2 in order
    When index 1 is removed from root list "tasks"
    Then the root list "tasks" has member ids 3, 2 in order

  @milestone-lists @single-user
  Scenario: create plus listInsert in one commit
    Given the app description:
      """
      types
          Db
              tasks list of Task
          Task
              title text
      """
    When the store is opened
    And a new Task titled "fresh" is created and inserted at index 0 of root list "tasks"
    Then the root list "tasks" has one member
    And the extent "Task" contains exactly 1 object
