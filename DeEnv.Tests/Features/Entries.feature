Feature: Collection entry create and delete
  Adding members to a set and entries to a dictionary, and deleting them, on the
  Milestone 2 CRM instance. The self-hosted set/dict tables gate creation behind a
  `+ New` button that reveals a labeled create form; Save persists (set members on
  /customers, dictionary entries on /settings). A duplicate dictionary key is rejected
  with an error shown in the still-open create form. There is no create URL, so an
  entry keyed "new" is unaffected; navigating into a dictionary entry still resolves.

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
    And I fill the new key with "currency"
    And I fill the new "value" with "USD"
    And I add the dict entry
    Then a dict row eventually shows "currency"
    And the dict entry "currency" eventually has value "USD"

  @milestone-2 @single-user
  Scenario: A duplicate manual key is rejected
    Given a setting "currency" with value "USD"
    When I navigate to "/settings"
    And I fill the new key with "currency"
    And I fill the new "value" with "EUR"
    And I add the dict entry
    Then I see a create error

  @milestone-2 @single-user
  Scenario: An existing entry keyed "new" is viewable
    Given a setting "new" with value "hello"
    When I navigate to "/settings/new"
    Then the entry value shows "hello"

  @milestone-2 @single-user
  Scenario: Delete an entry
    Given a setting "currency" with value "USD"
    When I navigate to "/settings"
    And I remove the dict row "currency"
    And I navigate to "/settings/currency"
    Then I see a not-found view
