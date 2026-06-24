@milestone-auth
Feature: The access floor (read enforcement by principal)
  Access is a deny-by-default ruleset over the object model, enforced at the kernel
  floor below Code: a rule's condition is a Code expression the existing interpreter
  evaluates over { currentUser, object }. Objects a reader is not allowed to read
  never enter the db graph that ships to the client. With no rules the app is dormant
  (allow-all, today's behavior).

  Background:
    Given an app whose Db holds a set of "Milestone" and a "User" with a "role" enum (Admin, Member)
    And the access rule "Milestone read where currentUser.role == \"Admin\""
    And a seeded admin user and a seeded member user
    And one seeded "Milestone" titled "Gate #3"

  Scenario: An admin principal can read the ruled type
    Given the current user is the admin
    When the page state is rendered for "/"
    Then the shipped data includes a "Milestone" titled "Gate #3"

  Scenario: A non-admin principal is denied the ruled type
    Given the current user is the member
    When the page state is rendered for "/"
    Then the shipped data includes no "Milestone"

  Scenario: An anonymous principal is denied (the condition fails closed, never errors)
    Given there is no current user
    When the page state is rendered for "/"
    Then the render does not error
    And the shipped data includes no "Milestone"

  Scenario: With no access rules the app is dormant and everything loads
    Given the app has no access rules
    And the current user is the member
    When the page state is rendered for "/"
    Then the shipped data includes a "Milestone" titled "Gate #3"
