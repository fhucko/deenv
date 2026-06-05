Feature: URL navigation and rendering
  Navigating an instance whose Db is an object containing a dictionary.
  Exercises the core model: forms for objects, tables for dictionaries,
  breadcrumbs mirroring the path, and not-found for unresolvable URLs.

  Background:
    Given an instance whose Db is an object "Shop"
    And Shop has a field "customers" that is a dictionary of Customer
    And Customer is an object with fields "name" (text) and "active" (bool)
    And customers contains key "42" with name "Acme" and active true

  @milestone-1 @single-user
  Scenario: The root object renders as a form
    When I navigate to "/"
    Then I see a form for "Db"
    And the "customers" field renders as a table

  @milestone-1 @single-user
  Scenario: A dictionary entry renders as its own form
    When I navigate to "/customers/42"
    Then I see a form for "Customer"
    And the "name" field shows "Acme"
    And the "active" field shows a checked checkbox

  @milestone-1 @single-user
  Scenario: A table row links to the entry by key
    When I navigate to "/"
    And I click the row for key "42" in the customers table
    Then the URL is "/customers/42"
    And I see a form for "Customer"

  @milestone-1 @single-user
  Scenario: Breadcrumbs mirror the path
    When I navigate to "/customers/42"
    Then the breadcrumbs read "Db / customers / 42"

  @milestone-1 @single-user
  Scenario: A deleted key resolves to not-found
    Given customers has no key "99"
    When I navigate to "/customers/99"
    Then I see a not-found view
    And the breadcrumbs let me return to "/customers"
