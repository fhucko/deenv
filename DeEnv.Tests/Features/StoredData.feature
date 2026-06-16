Feature: Stored data matches the running app
  Each app document owns its own data file, derived from the app file's name, so
  switching apps never mixes data. On startup the store validates an existing
  data file against the running app's types and REFUSES to start on a mismatch —
  loudly, with the file and the offending detail named — instead of silently
  running over stale data. It never reseeds over an existing file: deleting or
  moving the file is a deliberate user act.

  # ── data file per app ──────────────────────────────────────────────────────

  @milestone-8 @single-user @persistence
  Scenario Outline: Each app document gets its own data file
    When the data file name is derived for app "<app>"
    Then the data file name is "<dataFile>"

    Examples:
      | app                    | dataFile           |
      | instance.app           | instance-data.json |
      | crm.app                | crm-data.json      |
      | C:\elsewhere\shop.app  | shop-data.json     |

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
