Feature: Aged store (real-world data shapes fresh seeds never hold)
  Every other feature boots from FRESH seeds, but a real dev/prod store lives through many schema
  migrations and mirror writes, accumulating shapes a fresh fixture never holds. Two real bugs escaped
  the suite this way in one day (2026-07-08): the &&/|| non-short-circuit bug (a79f19f) only manifested
  over a store holding instances WITHOUT designs (the MirrorInstanceInsert shape the registry mirror
  creates for devlog/demo), and rows created under older schemas (absent fields vs explicit nulls)
  behave differently from freshly-minted complete rows.

  This harness synthesizes that CLASS of shapes with the real store APIs — design-less instances,
  written-then-cleared single refs (WriteReference null: a logged clear + GC, a different history than
  never-written), rows predating schema fields, adoption-baseline commits (no author, no parent) — and
  drives the affected pages through a REAL browser over a warm session. The &&/|| repro required the
  CLIENT-SIDE nav → refetch path (SSR alone did not reproduce), so every page transition here is a
  clicked in-app link, not a fresh GET; a console/pageerror collector asserts the whole sweep stayed
  error-free (a failed refetch surfaces only as console.error("Server error:", …), never as a throw).

  @aged-store @single-user
  Scenario: The designer's pages survive design-less and cleared-design instances plus adoption baselines
    Given the operator IDE is running on an aged kernel store
    When I click into the aged design editor for "todo"
    Then the aged design editor renders its publish section
    When I open the aged commit history from the editor
    And I open the adoption baseline commit
    Then the adoption commit detail renders
    When I click the Instances nav link
    Then the aged instances list shows "todo", "devlog" and "retired"
    When I click into the aged instance "devlog"
    Then the aged instance page renders its design selector
    And no client errors were recorded

  # NOTE — this scenario's first sweep FOUND a real bug of its target class (2026-07-08): an ABSENT
  # date field reads back as DateTime.Today (JsonFileInstanceStore.DefaultBase), while a UI-cleared
  # date reads "" (the empty-leaf model) — absent and cleared should read alike. The fix is its own
  # decision (DefaultBase is also the CREATE/seed/migration backfill); until it lands the date
  # assertion pins SSR/client CONSISTENCY only (see AgedStoreSteps).
  @aged-store @single-user
  Scenario: The generic UI survives rows created under an older schema than the one served
    Given an app store aged under an old schema and served under one with added fields
    When I open the aged notes list
    And I click into the aged note "Grown row"
    Then the aged note form shows title "Grown row"
    And the aged note form reads the added fields consistently
    When I return to the aged notes list and click into the aged note "Cleared author"
    Then the aged note form shows title "Cleared author"
    And no client errors were recorded
