Feature: Presentation and form refinements
  Readability and correctness refinements on the rendered instance: humanized
  field labels, navigable list titles, bordered tables, and a flag-gated create form
  (revealed by `+ New`) that omits set/dictionary (navigation-boundary) fields.

  Background:
    Given a CRM instance

  @milestone-2 @single-user
  Scenario: Field labels are humanized
    When I navigate to "/"
    Then I see the label "Company name"

  @milestone-2 @single-user
  Scenario: A list title links to its dictionary
    When I navigate to "/"
    And I click the "Customers" list title
    Then the URL is "/customers"

  @milestone-2 @single-user
  Scenario: The create form omits dictionary fields
    When I navigate to "/customers"
    And I click New
    Then the create form has a "name" field
    And the create form has no "orders" field
