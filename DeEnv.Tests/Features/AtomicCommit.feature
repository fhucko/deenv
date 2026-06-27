@milestone-atomic-commit
Feature: Atomic ctx.commit (edits)
  ctx.commit() sends ONE `commit` message the server applies all-or-none. If any edit in the batch
  fails validation (unknown prop, access denial, bad enum), NO edit in the batch persists. Before
  this change, each field was sent as an independent `objectPropChange`; a partial batch left the
  successful edits persisted while the failed one was rejected — the commit was not the unit.

  Background:
    Given the two-field commit fixture app

  Scenario: A commit where all edits are valid persists every field
    When the commit sends title "Updated title" and count 7
    Then the commit is accepted
    And the stored "Item" 2 has "title" equal to "Updated title"
    And the stored "Item" 2 has "count" equal to 7

  Scenario: A commit where one edit names a non-existent field rolls back the whole batch
    When the commit sends title "Would persist" and an unknown field "noSuchProp"
    Then the commit is rejected
    And the stored "Item" 2 has "title" equal to "Seed title"

  Scenario: A commit denied by an access rule rolls back every edit atomically
    Given the Item access rule "Item edit where currentUser.role == \"Admin\""
    And the current user is the member
    When the member commits edits to both "title" and "count" of item 2
    Then the commit is rejected
    And the stored "Item" 2 has "title" equal to "Seed title"
    And the stored "Item" 2 has "count" equal to 0

  # ── Step B: the atomic changeset (edits + a new object of ANOTHER type + a relation) ──
  # The headline: a custom render stages, in ONE ctx, an edit to an EXISTING object, a CREATE of a new
  # object of a SEPARATE unrelated type, and the RELATION linking it — committed all-or-none. A connected
  # parent+children graph is just one shape of this; the requirement is atomicity over an arbitrary,
  # possibly-unrelated changeset. The `commit` op is driven directly at the WsHandler level (in-process).

  Scenario: An atomic changeset of an edit + a new object + a relation persists all with real ids
    Given the atomic-changeset fixture app
    When the changeset edits item 2 title to "Changed title", creates a Tag "release" and links it into tags
    Then the commit is accepted
    And the stored "Item" 2 has "title" equal to "Changed title"
    And a "Tag" labelled "release" exists in the store
    And the "tags" set contains a "Tag" labelled "release"
    And the commit reply maps the new Tag to a real id

  # The all-or-none teeth: ONE denied change in the batch rolls the WHOLE changeset back — no orphan Tag
  # in the extent, and the Item edit reverted. Before atomic commit, the live set.add minted the Tag
  # before any edit floor ran, so a denied sibling left an orphan object behind.
  Scenario: A changeset with one denied change persists nothing — no orphan object, no partial graph
    Given the atomic-changeset fixture app denying Tag create
    And the current user is the member
    When the member's changeset edits item 2 title to "Sneaky", creates a Tag "ghost" and links it into tags
    Then the commit is rejected
    And the stored "Item" 2 has "title" equal to "Seed title"
    And no "Tag" labelled "ghost" exists in the store

  # The flat-remap invariant's teeth: a relation that references a create tempId NOT present in the batch
  # is a malformed changeset — the WHOLE commit is rejected and the store is left untouched (no half-applied
  # link, no leaked id). Pins the store's own all-or-none guard (a negId never resolves to a real id).
  Scenario: A changeset whose relation references a missing create is rejected and persists nothing
    Given the atomic-changeset fixture app
    When a changeset links a non-existent create into tags
    Then the commit is rejected
    And the "tags" set is empty

  # The password-hash chokepoint (SECURITY): a staged User create carrying a plaintext password is PBKDF2-
  # hashed before the store, exactly like every other create path — a staged create can never store plaintext.
  Scenario: A staged User create hashes its password before the store
    Given the atomic-changeset fixture app
    When a changeset creates a User "Carol" with password "s3cret" and links it into users
    Then the commit is accepted
    And the stored "User" "Carol" password is not the plaintext "s3cret"
    And the stored "User" "Carol" password verifies against "s3cret"

  # SECURITY (floor-widening guard): a create has exactly one join. A forged message linking ONE create's
  # tempId into TWO sets would let the create be floor-checked as one type but linked as another (the
  # create-type would be last-write-wins) — so a tempId named by more than one relation is REJECTED whole,
  # the store untouched. The interpreter never emits this; only a hand-forged WS message can.
  Scenario: A changeset linking one create into two sets is rejected and persists nothing
    Given the atomic-changeset fixture app
    When a changeset links one create into both tags and users
    Then the commit is rejected
    And the "tags" set is empty
    And no "Tag" labelled "forged" exists in the store
