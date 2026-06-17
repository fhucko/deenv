Feature: Transient id remap (editing a just-added object before its round-trip)
  When the Code UI adds an object to a set, the client mints it with a transient NEGATIVE id and keeps
  addressing it by that id — as the objectId of a field edit, the member id of a remove — until the
  arrayAdd round-trip remaps it to the real one. The client must be free to fire those follow-up ops
  immediately, with the negative id; the server reconciles. The server keeps a per-client (per-session)
  map of every negative→real id it assigned when it minted an added object, and resolves each inbound id
  through it — so an op addressing a just-added object's transient id lands on the real object instead of
  being rejected. Ordered WebSocket delivery guarantees the minting arrayAdd is processed before any op
  that references its tempId, so the mapping is always present by the time it is needed. Driven at the
  WS-handler seam (real store + a live client session), no browser. Milestone 10.

  @milestone-10 @single-user
  Scenario: A field edit addressing a just-added object's transient id persists
    Given a Code instance with a set of items and a live client session
    When the client adds an item with transient id -5 over the WS
    And the client sets prop "name" on object -5 to "Status" over the WS
    Then the WS reply is ok
    And the added member has name "Status"

  @milestone-10 @single-user
  Scenario: Removing a just-added object by its transient id removes the real object
    Given a Code instance with a set of items and a live client session
    When the client adds an item with transient id -5 over the WS
    And the client removes object -5 from the set over the WS
    Then the WS reply is ok
    And the set has no members

  @milestone-10 @single-user
  Scenario: A real (positive) id still addresses its object directly
    Given a Code instance with a set of items and a live client session
    When the client adds an item with transient id -5 over the WS
    And the client sets prop "name" on the added member's real id to "Direct" over the WS
    Then the WS reply is ok
    And the added member has name "Direct"

  # The remap only resolves ids it actually assigned: a transient id the server never minted is still a
  # genuine "no such object" reject, not a silent accept — so a stray/garbage negative id cannot persist.
  @milestone-10 @single-user
  Scenario: An unmapped transient id is still rejected
    Given a Code instance with a set of items and a live client session
    When the client sets prop "name" on object -999 to "X" over the WS
    Then the WS reply is an error

  # A mapping lives only until the client acks it has applied the remap (it now uses the real id and never
  # sends the transient one again), so the table stays bounded to the in-flight adds. After the ack, the
  # transient id is dead: a stray op still addressing it is a genuine reject, not a silent accept.
  @milestone-10 @single-user
  Scenario: After the client acks the new id the server drops the mapping
    Given a Code instance with a set of items and a live client session
    When the client adds an item with transient id -5 over the WS
    And the client acks the new id for transient id -5 over the WS
    And the client sets prop "name" on object -5 to "Stale" over the WS
    Then the WS reply is an error
