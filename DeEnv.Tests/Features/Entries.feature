Feature: Dictionary entry create and delete
  Creating entries via a transient form (auto and manual keys) and deleting
  them, on the Milestone 2 CRM instance. Creation happens only on the create
  form's Save; there is no create URL, so an entry keyed "new" is unaffected.

  Background:
    Given a CRM instance

  @milestone-2 @single-user
  Scenario: Save and open creates the entry and opens it
    When I navigate to "/customers"
    And I click New
    And I set the create field "name" to "NewCo"
    And I set the create field "email" to "n@newco.com"
    And I save and open the create form
    Then the URL matches "/customers/\d+$"
    And the "name" field shows "NewCo"

  @milestone-2 @single-user
  Scenario: Save creates the entry and returns to the list
    When I navigate to "/customers"
    And I click New
    And I set the create field "name" to "NewCo"
    And I save the create form
    Then the URL is "/customers"
    And the table has 1 rows

  @milestone-2 @single-user
  Scenario: Cancelling the create form adds nothing
    When I navigate to "/customers"
    And I click New
    And I cancel the create form
    And I navigate to "/customers"
    Then the table has 0 rows

  @milestone-2 @single-user
  Scenario: Create a setting with a manual key
    When I navigate to "/settings"
    And I click New
    And I set the create key to "currency"
    And I set the create value to "USD"
    And I save and open the create form
    Then the URL is "/settings/currency"

  @milestone-2 @single-user
  Scenario: A duplicate manual key is rejected
    Given a setting "currency" with value "USD"
    When I navigate to "/settings"
    And I click New
    And I set the create key to "currency"
    And I set the create value to "EUR"
    And I save the create form
    Then I see a create error

  @milestone-2 @single-user
  Scenario: An existing entry keyed "new" is viewable
    Given a setting "new" with value "hello"
    When I navigate to "/settings/new"
    Then the "new" field shows "hello"

  @milestone-2 @single-user
  Scenario: Delete an entry
    Given a setting "currency" with value "USD"
    When I navigate to "/settings"
    And I delete the row for key "currency"
    And I navigate to "/settings/currency"
    Then I see a not-found view
