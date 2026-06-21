Feature: Todo app
  The committed default app (DeEnv/instances/2/app.app): users own todo lists, lists
  own items. Rebuilt as the M11 auto-with-overrides showcase — a hand-written custom
  `fn render()` that COMPOSES the public component library: each item row composes the
  library `<Input>` primitive for its checkbox and its text field (the same control the
  generic object form uses), inside a custom card/checklist layout (the "overrides").
  Driven end-to-end through a real browser: SSR first paint, client hydration, optimistic
  mutations persisted over the WS, and selection-driven rendering.

  @milestone-11
  Scenario: First paint renders the seeded user
    Given the todo app is running
    Then the page shows the user "User 1"

  @milestone-11
  Scenario: Selecting a user shows their lists as cards
    Given the todo app is running
    When I select the user "User 1"
    Then the page shows the list "List 1"

  @milestone-11
  Scenario: Adding an item shows it and persists it
    Given the todo app is running
    When I select the user "User 1"
    And I add a new item "Buy milk" to the list "List 1"
    Then the page shows an item "Buy milk"
    And the store eventually has a "TodoItem" whose "text" is "Buy milk"

  @milestone-11
  Scenario: Checking an item marks it done and persists
    Given the todo app is running
    When I select the user "User 1"
    And I add a new item "Buy milk" to the list "List 1"
    And I check the item "Buy milk"
    Then the item "Buy milk" is checked
    And the store eventually has a checked "TodoItem"

  @milestone-11
  Scenario: Removing an item takes it off the list
    Given the todo app is running
    When I select the user "User 1"
    And I add a new item "Buy milk" to the list "List 1"
    And I remove the item "Buy milk"
    Then the page does not show an item "Buy milk"

  @milestone-11
  Scenario: Adding a list to the selected user shows a new card
    Given the todo app is running
    When I select the user "User 1"
    And I add a new list "Groceries"
    Then the page shows the list "Groceries"
    And the store eventually has a "TodoList" whose "name" is "Groceries"

  @milestone-11
  Scenario: Adding a user and selecting it
    Given the todo app is running
    When I add a new user "User 2"
    Then the page shows the user "User 2"
    And the store eventually has a "User" whose "name" is "User 2"
    When I select the user "User 2"
    Then the page shows the selected user "User 2"

  # The blank-object factories mint drafts via sys.new(sys.schema(T)), which OMITS a type's
  # collection props (the store materializes them on add). This guards that path end-to-end on
  # the CLIENT: a freshly-created user gets a list nested into its (store-materialized) todoLists,
  # and that list gets an item nested into its items — and both persist.
  @milestone-11
  Scenario: Nesting into a freshly-created user and list
    Given the todo app is running
    When I add a new user "User 2"
    And I select the user "User 2"
    And I add a new list "Groceries"
    And I add a new item "Buy milk" to the list "Groceries"
    Then the page shows the list "Groceries"
    And the page shows an item "Buy milk"
    And the store eventually has a "TodoList" whose "name" is "Groceries"
    And the store eventually has a "TodoItem" whose "text" is "Buy milk"
