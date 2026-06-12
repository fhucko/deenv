Feature: UI customization (views)
  An app's `ui` section can define views — a render function bound to a type or a
  URL path — without taking over the whole app. A TYPE view replaces the generic
  object page for that type (the breadcrumb chrome stays); a PATH view owns a URL
  subtree; everything else stays the generic auto-form. The shop app (a custom
  Customer page + a /dashboard path view over a generic remainder, no `fn render`)
  is the proof.

  @milestone-8 @single-user
  Scenario: A type view replaces the generic object page
    Given the shop app is running
    When I open "/customers/2"
    Then the page is a code page
    And the page shows ".customer-card"
    And the page shows the breadcrumbs
    And the page shows the open order "Analytical Engine"
    And the page does not show the open order "Punch cards"

  @milestone-8 @single-user
  Scenario: Sibling pages without a view stay generic
    Given the shop app is running
    When I open "/"
    Then the page is a generic auto-form
    When I open "/customers/2/orders/3"
    Then the page is a generic auto-form

  @milestone-8 @single-user
  Scenario: A path view owns its URL
    Given the shop app is running
    When I open "/dashboard"
    Then the page is a code page
    And the page shows ".dashboard"
    And the active customer "Ada Lovelace" is listed
    And the active customer "Grace Hopper" is listed

  @milestone-8 @single-user
  Scenario: Editing on a type-view page persists over the WebSocket
    Given the shop app is running
    When I open "/customers/2"
    And I set the email to "ada@deenv.dev"
    Then the store eventually has a "Customer" whose "email" is "ada@deenv.dev"

  @milestone-8 @single-user
  Scenario: A type-view edit is reflected by a sibling path view
    Given the shop app is running
    When I open "/customers/2"
    And I uncheck active
    And I open "/dashboard"
    Then the active customer "Grace Hopper" is listed
    And the active customer "Ada Lovelace" is not listed

  @milestone-8 @single-user
  Scenario: An app with only views (no render) loads
    Given the shop app is running
    When I open "/dashboard"
    Then the page is a code page
