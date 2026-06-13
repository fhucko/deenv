Feature: App description document
  An instance is defined by ONE app text document — types, optional initialData,
  and optional code sections (JSON is internal only: the in-memory model and the
  wire). The document is loaded and validated: a well-formed document loads, and
  a malformed one is rejected with a clear, specific error rather than failing
  obscurely later.

  @milestone-3 @single-user
  Scenario: A well-formed document loads
    Given the app description:
      """
      types
          Db
              companyName: text
              customers: set of Customer
          Customer
              name: text
              active: bool
      """
    When the document is loaded
    Then the document loads successfully
    And the root type is named "Db"

  @milestone-3 @single-user
  Scenario: A base-typed root is rejected — Db must be an object
    Given the app description:
      """
      types
          Db: bool
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "object"

  @milestone-3 @single-user
  Scenario: A document with no Db type is rejected
    Given the app description:
      """
      types
          Thing: bool
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "Db"

  @milestone-3 @single-user
  Scenario: Two types sharing a name are rejected
    Given the app description:
      """
      types
          Db: bool
          Thing: bool
          Thing: text
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "Thing"

  @milestone-3 @single-user
  Scenario: A duplicate Db is rejected by the same duplicate-name rule
    Given the app description:
      """
      types
          Db: bool
          Db: text
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "Db"

  @milestone-3 @single-user
  Scenario: A prop referencing an unknown type is rejected
    Given the app description:
      """
      types
          Db
              customers: dict of Customer by text
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "Customer"

  @milestone-3 @single-user
  Scenario: An unknown base type is rejected
    Given the app description:
      """
      types
          Db: money
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "baseType"

  @milestone-3 @single-user
  Scenario: Duplicate prop names within a type are rejected
    Given the app description:
      """
      types
          Db
              name: text
              name: bool
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "name"

  @milestone-3 @single-user
  Scenario: A syntactically broken document is rejected with its position
    Given the app description:
      """
      types
          Db
              name text
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "line"

  @milestone-5 @single-user
  Scenario: A set prop with a scalar element type is rejected
    Given the app description:
      """
      types
          Db
              tags: set of text
      """
    When the document is loaded
    Then loading is rejected with an error mentioning "set"

  @milestone-3 @single-user
  Scenario: The instance is defined by an app description file
    Given an app description file describing a single-bool Db
    When the instance is started from that file
    Then the instance is running
    And its checkbox is visible
