Feature: Stored data matches the running app
  Each instance owns its own data file (instances/<id>/app-data.json), so
  switching apps never mixes data. On startup the store validates an existing
  data file against the running app's types and REFUSES to start on a mismatch —
  loudly, with the file and the offending detail named — instead of silently
  running over stale data. It never reseeds over an existing file: deleting or
  moving the file is a deliberate user act.

  # ── the startup guard ──────────────────────────────────────────────────────

  @milestone-8 @single-user @persistence
  Scenario: A data file written for a different app is rejected at startup
    Given the app description:
      """
      types
          Db
              users set of User
          User
              name text
      """
    And a stored data file containing:
      """
      {
        "extents": {
          "Db": { "1": { "type": "object", "typeName": "Db", "id": 1, "fields": {
            "companyName": { "type": "text", "value": "Acme" } } } },
          "Customer": { "2": { "type": "object", "typeName": "Customer", "id": 2, "fields": {} } }
        },
        "nextId": 2,
        "root": { "type": "object", "typeName": "Db", "id": 1 }
      }
      """
    When the store is opened
    Then opening is rejected with a data error mentioning "Customer"

  @milestone-8 @single-user @persistence
  Scenario: A stored field the app does not declare is rejected
    Given the app description:
      """
      types
          Db
              name text
      """
    And a stored data file containing:
      """
      {
        "extents": {
          "Db": { "1": { "type": "object", "typeName": "Db", "id": 1, "fields": {
            "name": { "type": "text", "value": "ok" },
            "companyName": { "type": "text", "value": "stale" } } } }
        },
        "nextId": 1,
        "root": { "type": "object", "typeName": "Db", "id": 1 }
      }
      """
    When the store is opened
    Then opening is rejected with a data error mentioning "companyName"

  @milestone-8 @single-user @persistence
  Scenario: A stored field whose kind contradicts the declaration is rejected
    Given the app description:
      """
      types
          Db
              users set of User
          User
              name text
      """
    And a stored data file containing:
      """
      {
        "extents": {
          "Db": { "1": { "type": "object", "typeName": "Db", "id": 1, "fields": {
            "users": { "type": "text", "value": "not a set" } } } }
        },
        "nextId": 1,
        "root": { "type": "object", "typeName": "Db", "id": 1 }
      }
      """
    When the store is opened
    Then opening is rejected with a data error mentioning "users"

  @milestone-8 @single-user @persistence
  Scenario: A stored scalar whose value type contradicts the declaration is rejected
    Given the app description:
      """
      types
          Db
              count int
      """
    And a stored data file containing:
      """
      {
        "extents": {
          "Db": { "1": { "type": "object", "typeName": "Db", "id": 1, "fields": {
            "count": { "type": "text", "value": "three" } } } }
        },
        "nextId": 1,
        "root": { "type": "object", "typeName": "Db", "id": 1 }
      }
      """
    When the store is opened
    Then opening is rejected with a data error mentioning "count"

  @milestone-8 @single-user @persistence
  Scenario: A legacy data file without collection ids is rejected
    Given the app description:
      """
      types
          Db
              users set of User
          User
              name text
      """
    And a stored data file containing:
      """
      {
        "extents": {
          "Db": { "1": { "type": "object", "typeName": "Db", "id": 1, "fields": {
            "users": { "type": "set", "members": {} } } } }
        },
        "root": { "type": "object", "typeName": "Db", "id": 1 }
      }
      """
    When the store is opened
    Then opening is rejected with a data error mentioning "id"

  @milestone-8 @single-user @persistence
  Scenario: A reference to an object that does not exist is rejected
    Given the app description:
      """
      types
          Db
              users set of User
          User
              name text
      """
    And a stored data file containing:
      """
      {
        "extents": {
          "Db": { "1": { "type": "object", "typeName": "Db", "id": 1, "fields": {
            "users": { "type": "set", "id": 2, "members": {
              "9": { "type": "object", "typeName": "User", "id": 9 } } } } } }
        },
        "nextId": 2,
        "root": { "type": "object", "typeName": "Db", "id": 1 }
      }
      """
    When the store is opened
    Then opening is rejected with a data error mentioning "9"

  @milestone-8 @single-user @persistence
  Scenario: The rejection names the data file and the remedy
    Given the app description:
      """
      types
          Db
              name text
      """
    And a stored data file containing:
      """
      {
        "extents": {
          "Gone": { "2": { "type": "object", "typeName": "Gone", "id": 2, "fields": {} } }
        },
        "nextId": 2,
        "root": { "type": "object", "typeName": "Db", "id": 1 }
      }
      """
    When the store is opened
    Then opening is rejected with a data error mentioning the data file path
    And opening is rejected with a data error mentioning "Delete or move"

  # ── what the guard tolerates ───────────────────────────────────────────────

  @milestone-8 @single-user @persistence
  Scenario: Data the app wrote earlier passes the guard on restart
    Given the app description:
      """
      types
          Db
              users set of User
          User
              name text

      initialData
          Db 1
              users: [2]
          User 2
              name: "Ada"
      """
    When the store is opened
    And the store is opened again on the same data file
    Then the store opens successfully
    And reading "/users/2/name" returns text "Ada"

  @milestone-8 @single-user @persistence
  Scenario: A data file missing a newly declared prop still loads
    Given the app description:
      """
      types
          Db
              name text
              motto text
      """
    And a stored data file containing:
      """
      {
        "extents": {
          "Db": { "1": { "type": "object", "typeName": "Db", "id": 1, "fields": {
            "name": { "type": "text", "value": "Acme" } } } }
        },
        "nextId": 1,
        "root": { "type": "object", "typeName": "Db", "id": 1 }
      }
      """
    When the store is opened
    Then the store opens successfully
    And reading "/motto" returns text ""

  # decimal/date/datetime have no typed "empty" value (DateOnly/decimal/DateTimeOffset are
  # non-nullable), so an ABSENT field of one of those types (a row that predates the schema field —
  # exactly what a missing-newly-declared-prop reload is) must read the SAME canonical unset form a
  # UI-CLEARED field stores: the empty-text leaf. NEVER a fabricated 0/today/now — that would make an
  # old row look freshly created, and diverge absent from cleared.
  @milestone-13 @single-user @persistence
  Scenario: A data file missing newly declared decimal/date/datetime props reads them as unset
    Given the app description:
      """
      types
          Db
              name text
              price decimal
              due date
              seenAt datetime
      """
    And a stored data file containing:
      """
      {
        "extents": {
          "Db": { "1": { "type": "object", "typeName": "Db", "id": 1, "fields": {
            "name": { "type": "text", "value": "Acme" } } } }
        },
        "nextId": 1,
        "root": { "type": "object", "typeName": "Db", "id": 1 }
      }
      """
    When the store is opened
    Then the store opens successfully
    And reading "/price" returns text ""
    And reading "/due" returns text ""
    And reading "/seenAt" returns text ""

  # The CREATE/mint path (a brand-new object's fields, e.g. the store's own bootstrap of the root
  # object with no initialData — the same field-building code path a real object creation uses):
  # decimal/date/datetime scalars mint as UNSET, never a fabricated 0/today/now. A freshly created
  # row must not look pre-filled with data nobody entered.
  @milestone-13 @single-user @persistence
  Scenario: A freshly minted object's decimal/date/datetime fields start unset, not today
    Given the app description:
      """
      types
          Db
              name text
              price decimal
              due date
              seenAt datetime
      """
    When the store is opened
    Then the store opens successfully
    And reading "/price" returns text ""
    And reading "/due" returns text ""
    And reading "/seenAt" returns text ""
