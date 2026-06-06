Feature: Schema document
  An instance is defined by a JSON schema document. The document is loaded
  and validated: a well-formed document loads, and a malformed one is
  rejected with a clear, specific error rather than failing obscurely later.

  @milestone-3 @single-user
  Scenario: A well-formed document loads
    Given the schema document:
      """
      {
        "types": [
          {
            "name": "Db",
            "baseType": "object",
            "props": [
              { "name": "companyName", "type": "text" },
              { "name": "customers", "type": "Customer", "cardinality": "dictionary", "keyType": "int", "keyGeneration": "auto" }
            ]
          },
          {
            "name": "Customer",
            "baseType": "object",
            "props": [
              { "name": "name",   "type": "text" },
              { "name": "active", "type": "bool" }
            ]
          }
        ]
      }
      """
    When the document is loaded
    Then the document loads successfully
    And the root type is named "Db"

  @milestone-3 @single-user
  Scenario: The minimal bool-root document loads
    Given the schema document:
      """
      { "types": [ { "name": "Db", "baseType": "bool" } ] }
      """
    When the document is loaded
    Then the document loads successfully

  @milestone-3 @single-user
  Scenario: A document with no Db type is rejected
    Given the schema document:
      """
      { "types": [ { "name": "Thing", "baseType": "bool" } ] }
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "Db"

  @milestone-3 @single-user
  Scenario: Two types sharing a name are rejected
    Given the schema document:
      """
      {
        "types": [
          { "name": "Db", "baseType": "bool" },
          { "name": "Thing", "baseType": "bool" },
          { "name": "Thing", "baseType": "text" }
        ]
      }
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "Thing"

  @milestone-3 @single-user
  Scenario: A duplicate Db is rejected by the same duplicate-name rule
    Given the schema document:
      """
      {
        "types": [
          { "name": "Db", "baseType": "bool" },
          { "name": "Db", "baseType": "text" }
        ]
      }
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "Db"

  @milestone-3 @single-user
  Scenario: A prop referencing an unknown type is rejected
    Given the schema document:
      """
      {
        "types": [
          {
            "name": "Db",
            "baseType": "object",
            "props": [ { "name": "customers", "type": "Customer", "cardinality": "dictionary", "keyType": "text" } ]
          }
        ]
      }
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "Customer"

  @milestone-3 @single-user
  Scenario: An unknown base type is rejected
    Given the schema document:
      """
      { "types": [ { "name": "Db", "baseType": "money" } ] }
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "money"

  @milestone-3 @single-user
  Scenario: An object type with no props is rejected
    Given the schema document:
      """
      { "types": [ { "name": "Db", "baseType": "object" } ] }
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "props"

  @milestone-3 @single-user
  Scenario: A non-object type carrying props is rejected
    Given the schema document:
      """
      {
        "types": [
          { "name": "Db", "baseType": "bool", "props": [ { "name": "x", "type": "text" } ] }
        ]
      }
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "props"

  @milestone-3 @single-user
  Scenario: An auto-keyed dictionary with a non-int key type is rejected
    Given the schema document:
      """
      {
        "types": [
          {
            "name": "Db",
            "baseType": "object",
            "props": [ { "name": "items", "type": "text", "cardinality": "dictionary", "keyType": "text", "keyGeneration": "auto" } ]
          }
        ]
      }
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "auto"

  @milestone-3 @single-user
  Scenario: keyGeneration on a non-dictionary prop is rejected
    Given the schema document:
      """
      {
        "types": [
          {
            "name": "Db",
            "baseType": "object",
            "props": [ { "name": "label", "type": "text", "keyGeneration": "manual" } ]
          }
        ]
      }
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "keyGeneration"

  @milestone-3 @single-user
  Scenario: Duplicate prop names within a type are rejected
    Given the schema document:
      """
      {
        "types": [
          {
            "name": "Db",
            "baseType": "object",
            "props": [
              { "name": "name", "type": "text" },
              { "name": "name", "type": "bool" }
            ]
          }
        ]
      }
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "name"

  @milestone-3 @single-user
  Scenario: Syntactically invalid JSON is rejected clearly
    Given the schema document:
      """
      { "types": [ { "name": "Db", "baseType": "bool" }
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "JSON"

  @milestone-3 @single-user
  Scenario: The instance is defined by a schema document file
    Given a schema document file describing a single-bool Db
    When the instance is started from that file
    Then the instance is running
    And its checkbox is visible
