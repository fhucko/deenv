Feature: Collection entry create and delete
  Adding members to a set and entries to a dictionary, and deleting them, on the
  Milestone 2 CRM instance. The self-hosted set/dict tables show an inline add
  form that autosaves on Add (set members on /customers); the dictionary route
  (/settings) is still served by the C# auto-form with a transient create form.
  There is no create URL, so an entry keyed "new" is unaffected.

  Background:
    Given a CRM instance

  @milestone-2 @single-user
  Scenario: Adding a member to a set creates it in the list
    When I navigate to "/customers"
    And I fill the new "name" with "NewCo"
    And I add to the set
    Then a set row eventually shows "NewCo"

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
