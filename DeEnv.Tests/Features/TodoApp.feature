Feature: Todo app
  The committed default app (DeEnv/instance.schema.json): users own todo lists,
  lists own items. The Code milestone's end-to-end proof — hand-written ui AST +
  normalized initialData, driven through a real browser over the live server
  (SSR first paint, client hydration, WS persistence, lazy load via refetch).

  @milestone-code
  Scenario: First paint renders the seeded data
    Given the todo app is running
    Then the page shows the user "User 1"

  @milestone-code
  Scenario: Selecting a user lazily loads their lists from the server
    Given the todo app is running
    When I select the user "User 1"
    Then the page shows the list "List 1"

  @milestone-code
  Scenario: Adding an item shows it and persists it
    Given the todo app is running
    When I select the user "User 1"
    And I select the list "List 1"
    And I add a new item "Buy milk"
    Then the page shows an item "Buy milk"
    And the store eventually has a "TodoItem" whose "text" is "Buy milk"

  @milestone-code
  Scenario: Checking an item marks it done and persists
    Given the todo app is running
    When I select the user "User 1"
    And I select the list "List 1"
    And I add a new item "Buy milk"
    And I check the first item
    Then the page shows the done item "Buy milk"
    And the store eventually has a checked "TodoItem"

  @milestone-code
  Scenario: Adding and removing a user
    Given the todo app is running
    When I add a new user "User 2"
    Then the page shows the user "User 2"
    And the store eventually has a "User" whose "name" is "User 2"
    When I remove the user "User 2"
    Then the page does not show the user "User 2"

  @milestone-code
  Scenario: Navigating between pages via the path variable
    Given the todo app is running
    When I open the about page
    Then the page shows the about text
    When I open the users page
    Then the page shows the user "User 1"
