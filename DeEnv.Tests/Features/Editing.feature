Feature: Editing fields with explicit Save
  Editing an object's fields and committing them with the Save button.
  Nothing persists until Save. Exercises text, decimal, date, bool, and a
  nested order on the Milestone 2 CRM instance.

  Background:
    Given a CRM instance
    And a customer "1" with name "Acme" email "a@acme.com" active true

  @milestone-2 @single-user
  Scenario: Edit a customer's text fields and save
    When I navigate to the customer "1"
    And I set the "name" field to "Acme Corp"
    And I set the "email" field to "info@acme.com"
    And I save
    And I reload
    Then the "name" field shows "Acme Corp"
    And the "email" field shows "info@acme.com"

  @milestone-2 @single-user
  Scenario: Edit decimal and date on a nested order
    Given an order "1" of customer "1" with total "10"
    When I navigate to the order "1" of customer "1"
    And I set the "total" field to "99.5"
    And I set the "date" field to "2026-02-03"
    And I save
    And I reload
    Then the "total" field shows "99.5"
    And the "date" field shows "2026-02-03"

  @milestone-2 @single-user
  Scenario: An unsaved edit does not persist
    When I navigate to the customer "1"
    And I set the "name" field to "Temporary"
    And I reload
    Then the "name" field shows "Acme"

  @milestone-2 @single-user
  Scenario: Edit the root scalar field
    When I navigate to "/"
    And I set the "companyName" field to "Globex"
    And I save
    And I reload
    Then the "companyName" field shows "Globex"
