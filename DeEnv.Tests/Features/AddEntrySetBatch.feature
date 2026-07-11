@milestone-13
Feature: addEntry set-value mint+link batching (the orphan-atomicity + fsync-count fix)
  Adding a NEW object to a set through the generic-UI create-form (`addEntry` with a `value`, not a
  `refId` — HandleAddSetMember's value branch) mints the object and links it into the set as ONE atomic
  store mutation (one Save()/fsync) through CommitBatch, the same fix HandleArrayAdd already applies
  (61c9330) — not two separate mutating store calls (formerly CreateObject then AddToSet), which cost the
  op two physical fsyncs and left a crash window where a committed create with a failing link afterward
  persisted an unreachable (orphaned) object. The reported id/version, and the store's post-write shape,
  must be exactly what the old two-call path produced. Driven at the WS-handler seam (real store + a live
  client session), no browser — see TransientId.feature for the sibling arrayAdd-side pattern.

  Background:
    Given a Db instance with a root set of Item and a live client session

  @single-user
  Scenario: A value-branch set add mints exactly one linked object
    When the client addEntry a new item named "NewCo" at path "/items" over the WS
    Then the WS addEntry reply is ok
    And the Item extent has exactly 1 object
    And the items set has exactly 1 member, the one addEntry reported
    And the reported item's "name" is "NewCo"
    And the store version advanced by exactly 2

  @single-user
  Scenario: A value-branch add into a set-member-owned NESTED set resolves its owner correctly
    Given an item "Parent" already in the set
    When the client addEntry a new child named "Kid" into the parent's children path over the WS
    Then the WS addEntry reply is ok
    And the Child extent has exactly 1 object
    And the parent's children set has exactly 1 member, the one addEntry reported
    And the store version advanced by exactly 2

  @single-user
  Scenario: A crafted path naming a real but non-member id is rejected, nothing linked
    Given an item "Parent" already in the set
    And a second item that exists but is NOT in the set
    When the client addEntry a new child named "Ghost" into the second item's children path over the WS
    Then the WS addEntry reply is an error
    And the items set still has exactly 1 member

  @single-user
  Scenario: A value-branch add through a single-reference chain resolves its owner correctly
    Given the root's "lead" reference points at a Lead
    When the client addEntry a new note named "Followed up" into the lead's notes path over the WS
    Then the WS addEntry reply is ok
    And the Note extent has exactly 1 object
    And the lead's notes set has exactly 1 member, the one addEntry reported
    And the store version advanced by exactly 2
