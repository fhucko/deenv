Feature: The object model — identity, references, sets, GC
  The first slice of Milestone 5. The data stops being a containment tree and
  becomes a C#-style object graph: non-constant values (objects) carry an
  intrinsic int identity, live in a per-type extent, and are pointed at by
  references rather than owned. A `set` addresses its members by their own
  identity; a single object-typed prop is a single reference. Lifetime is by
  mark-sweep GC from the root — an object no reference can reach is collected.

  Scope of this slice: one extent type (Person), one set (people) and one single
  reference (lead) into it. Scalar dictionaries and dictionaries-of-objects
  elsewhere are untouched (later slices). The instance:
    Db   { people: set<Person>, lead: Person (single reference) }
    Person { name: text }

  Background:
    Given an object-graph instance

  # ── identity + extent + set addressing ─────────────────────────────────────

  @milestone-5 @single-user
  Scenario: A new object minted into a set is addressed by its own identity
    When I navigate to "/people"
    And I create a new "Person" named "Ada" in the set
    Then the URL matches "/people/\d+$"
    And the "name" field shows "Ada"

  @milestone-5 @single-user
  Scenario: An object's identity persists across a reload
    Given a person "Ada" in the extent referenced by the set "people"
    When I navigate to "/people"
    Then the set "people" lists "Ada"
    And following "Ada" in the set "people" opens the same object both times

  @milestone-5 @single-user
  Scenario: The id-route resolves a bare reference to its object
    Given a person "Ada" in the extent referenced by the set "people"
    When I open the id-route for "Ada"
    Then the "name" field shows "Ada"

  # ── references: pick existing vs create new (no ownership) ──────────────────

  @milestone-5 @single-user
  Scenario: Picking an existing object adds a reference, not a copy
    Given a person "Ada" in the extent referenced by the set "people"
    When I navigate to "/lead"
    And I pick the existing "Person" named "Ada"
    Then the extent "Person" has 1 object
    And the current reference is "Ada"

  @milestone-5 @single-user
  Scenario: Creating a new object through a reference mints it into the extent
    When I navigate to "/lead"
    And I create a new "Person" named "Grace" through the reference
    Then the extent "Person" has 1 object
    And the current reference is "Grace"

  # ── the shared-object proof: one object, many references ────────────────────

  @milestone-5 @single-user
  Scenario: The same object reached by two references is one object
    Given a person "Ada" in the extent referenced by the set "people"
    And the single reference "lead" references the person "Ada"
    When I navigate to "/people"
    And I follow the set member open link
    And I fill the "name" field with "Ada Lovelace"
    And I save
    And I navigate to "/lead"
    Then the current reference is "Ada Lovelace"

  # ── lifetime by GC ─────────────────────────────────────────────────────────

  @milestone-5 @single-user
  Scenario: Dropping the last reference collects the object
    Given a person "Ada" in the extent referenced by the set "people"
    When I remove "Ada" from the set "people"
    Then the extent "Person" has 0 objects
    And the id-route for "Ada" is not found

  @milestone-5 @single-user
  Scenario: A remaining reference keeps the object alive
    Given a person "Ada" in the extent referenced by the set "people"
    And the single reference "lead" references the person "Ada"
    When I remove "Ada" from the set "people"
    Then the extent "Person" has 1 object
    When I navigate to "/lead"
    Then the current reference is "Ada"
