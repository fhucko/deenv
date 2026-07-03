@milestone-13
Feature: Design snapshot — canonical text + identity map (the per-commit caches)
  A design commit caches two derived artifacts: the canonical printed app document and a
  name-path → intrinsic-id map over its types and props. Together they make design diffs
  rename-aware with zero replay: the text carries the content, the id map carries the
  M5 identity the by-name projection otherwise drops. This is DECISIONS.md "App versioning —
  the full design (M13 clump)" — the caches slice-4 diff/publish read from two commits'
  rows. Derived and rebuildable, never authoritative; server-only; no storage, wire, or UI
  in this slice — the builder is proven directly.

  Background:
    Given a designer store seeded with a design that has a Db with a notes set and a Note with title and count

  Scenario: The snapshot text is the canonical app document
    When a snapshot of the design is built
    Then the snapshot text round-trips through the printer
    And building the snapshot again yields byte-identical text

  Scenario: The id map keys every type and prop by name-path to its intrinsic id
    When a snapshot of the design is built
    Then the id map has exactly one entry per type and per prop
    And the entry for each type and prop matches its row id in the designer store

  Scenario: A rename changes the text but keeps the identity
    Given a snapshot of the design is built and set aside as the old snapshot
    When the Note type's title prop is renamed to "heading" in the designer store
    And a snapshot of the design is built
    Then the new snapshot text differs from the old snapshot text
    And the id under "Note.heading" in the new snapshot's id map equals the id under "Note.title" in the old snapshot's id map

  Scenario: Deleting a prop and adding a same-named one yields a different identity
    Given a snapshot of the design is built and set aside as the old snapshot
    When the Note type's title prop is removed and a new prop named "title" is added in the designer store
    And a snapshot of the design is built
    Then the id under "Note.title" in the new snapshot's id map differs from the id under "Note.title" in the old snapshot's id map

  Scenario: An invalid design yields no snapshot
    Given the Note type's name is blanked in the designer store
    Then building a snapshot fails with a schema validation error
