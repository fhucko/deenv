Feature: Instance
  An instance is loaded and served. For milestone 1 the instance is
  hardcoded: a Db that is a single bool. No IDE, no editing.

  @milestone-1 @single-user
  Scenario: An instance starts
    Given a single-bool instance
    When it is started
    Then the instance is running
    And its checkbox is visible

  @milestone-1 @single-user
  Scenario: A fresh instance has a default value
    Given a single-bool instance that has never been edited
    When I open it
    Then the checkbox is unchecked

  @milestone-future @multi-instance
  Scenario: More than one instance can be served
    Given two separate single-bool instances
    When both are started
    Then each instance keeps its own independent value
