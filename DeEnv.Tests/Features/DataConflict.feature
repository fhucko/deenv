@milestone-13
Feature: Data conflicts — field-level overlap, disjoint auto-merge, and the coarse resolution UI
  A stale commit is no longer a dead-end error (M13 slice 6 — DECISIONS.md / app-versioning-design.md §2).
  When two sessions edit the SAME object from the same base, the server compares WHICH FIELDS each wrote:
  disjoint edits AUTO-MERGE (both survive, no reject, no retry storm); only a same-field COLLISION is
  rejected, and then with a per-field {base, mine, theirs} payload — base read straight from the durable
  log. The generic form renders a coarse banner (Keep mine = force re-commit at the current base; Take
  theirs = drop mine). A custom render that SURFACES the conflict (composing ObjectForm's ConflictBar, or
  reading ctx.conflicts itself) shows the in-form bar — and the redundant global "reload" banner is
  suppressed (B5 fast-follow, keyed on "did any render surface it"). The global banner remains the
  no-silent-clobber FALLBACK for a conflict surfaced by NO render (only clearly reachable via
  navigate-away-mid-reply — a same-field conflict needs a staging ctx, and only ObjectForm opens a
  conflict-carrying staging ctx today, so it is the door and it always surfaces). So no app can silently clobber.

  # ── (1) disjoint interleave auto-merges — the design's headline: stale base ≠ conflict ────────────────
  # Session A commits note 2's TITLE at a stale base while session B changed note 2's COUNT. Different
  # fields → no overlap → A auto-merges (applies) with NO error and NO retry. Both values persist.
  @multi-user
  Scenario: A stale commit whose fields are disjoint from the interleaved change auto-merges
    Given the conflict fixture app
    And two conflict sessions loaded the store at the current version
    When conflict session 1 commits note 2's count to 5 at its base
    Then conflict session 1's commit is accepted
    When conflict session 2 commits note 2's title to "Session 2 title" at its base
    Then conflict session 2's commit is accepted
    And conflict note 2's stored title is "Session 2 title"
    And conflict note 2's stored count is 5

  # ── (2) same-field collision rejects with the {base, mine, theirs} payload, base proven from the log ──
  # Both sessions loaded at version 0 (note 2 title "Note two"). Session 1 sets title "First". Session 2,
  # still at base 0, sets title "Second": the SAME field → a collision → rejected with a conflict payload
  # whose base is what session 2's draft saw ("Note two", the log's old value), mine is "Second", theirs
  # is session 1's landed "First".
  @multi-user
  Scenario: A same-field collision is rejected with a per-field base/mine/theirs payload
    Given the conflict fixture app
    And two conflict sessions loaded the store at the current version
    When conflict session 1 commits note 2's title to "First" at its base
    Then conflict session 1's commit is accepted
    When conflict session 2 commits note 2's title to "Second" at its base
    Then conflict session 2's commit is rejected as a conflict
    And the conflict payload names field "title" on note 2
    And the conflict's base is "Note two"
    And the conflict's mine is "Second"
    And the conflict's theirs is "First"

  # ── (5) multi-object batch: one conflicted field blocks the WHOLE batch (all-or-none) ─────────────────
  # Session 2 commits TWO edits at once: note 2's title (which session 1 collided on) AND note 3's title
  # (untouched). The one collision rejects the ENTIRE batch — note 3 is NOT written — and the payload
  # lists exactly the conflicted field (note 2's title, not note 3's). Re-committing at the fresh version
  # then applies the whole batch.
  @multi-user
  Scenario: One conflicted field in a multi-object batch blocks the whole batch, then applies once resolved
    Given the conflict fixture app
    And two conflict sessions loaded the store at the current version
    When conflict session 1 commits note 2's title to "One took it" at its base
    Then conflict session 1's commit is accepted
    When conflict session 2 commits note 2's title "Two's title" and note 3's title "Two's other" in one batch at its base
    Then conflict session 2's commit is rejected as a conflict
    And the conflict payload names field "title" on note 2
    And the conflict payload does not name note 3
    And conflict note 3's stored title is "Note three"
    When conflict session 2 re-commits that batch at the current version
    Then conflict session 2's commit is accepted
    And conflict note 2's stored title is "Two's title"
    And conflict note 3's stored title is "Two's other"

  # ── (6) set adds from two sessions with the same stale base both land (commute) ───────────────────────
  # Two sessions each add a DIFFERENT new note to db.notes from the same base. Set add/remove COMMUTE —
  # membership changes never collide — so both land with no conflict, even though the second is "stale".
  @multi-user
  Scenario: Set adds from two sessions at the same base both land (commute, no conflict)
    Given the conflict fixture app
    And two conflict sessions loaded the store at the current version
    When conflict session 1 adds a new note titled "From one" to the notes set at its base
    Then conflict session 1's commit is accepted
    When conflict session 2 adds a new note titled "From two" to the notes set at its base
    Then conflict session 2's commit is accepted
    And the conflict notes set has 4 members

  # ── (8) a commit with no baseVersion behaves exactly as before (legacy path untouched) ────────────────
  # An edit committed with NO baseVersion is never conflict-checked — it applies, last-write-wins, exactly
  # as before this slice (the version-less lower-level callers rely on this).
  Scenario: A commit with no baseVersion applies without any conflict check
    Given the conflict fixture app
    And one conflict session loaded the store at the current version
    When that conflict session commits note 2's title to "Base-pinned" at its base
    Then conflict session 1's commit is accepted
    When another conflict session commits note 2's title to "No base at all" with no base version
    Then conflict session 2's commit is accepted
    And conflict note 2's stored title is "No base at all"

  # ── (3) the generic form's coarse banner: Take theirs drops the draft, then a fresh edit commits ──────
  # Two real browser sessions on the SAME note. Session 1 changes the title and saves (lands). Session 2,
  # holding the pre-change data, changes the title to something else and saves → the in-form conflict bar
  # appears naming the field; the Save/Discard row is HIDDEN while the bar is up (fix 2 — the bar's two
  # buttons are the complete decision set, so a plain Save can never become a hidden force-overwrite). The
  # GENERIC form does NOT ALSO raise the global "reload" banner (B5 fine-conflict fast-follow): the generic
  # UI always renders the in-form ConflictBar, so a global "reload to see the latest" banner would be
  # redundant AND contradictory (reload = discard the very draft the bar is helping resolve). The global
  # banner stays the no-silent-clobber fallback for CUSTOM renders ONLY (scenario 7). Take theirs drops
  # session 2's draft and shows session 1's value, clears the in-form bar + its OWN transient confirmation
  # (fix 4b); the Save row reappears; a fresh edit then commits cleanly (its base is now the post-conflict
  # fresh version).
  @multi-user
  Scenario: The in-form bar appears with no global reload banner; Take theirs drops the draft and a fresh edit then commits
    Given the conflict fixture app is served
    And conflict session 1 opens the note at "/notes/2"
    And conflict session 2 opens the note at "/notes/2"
    When conflict session 1 saves the title "Session 1 wins"
    Then conflict session 1's save lands in the store
    When conflict session 2 saves the title "Session 2 tried"
    Then conflict session 2 sees the conflict banner naming "title"
    And conflict session 2's global error banner is gone
    And conflict session 2's Save button is hidden while the bar shows
    When conflict session 2 clicks Take theirs
    Then conflict session 2's title field shows "Session 1 wins"
    And conflict session 2's conflict banner is gone
    And conflict session 2's global error banner is gone
    And conflict session 2 sees the "Updated to latest" confirmation
    And conflict session 2's Save button is visible again
    When conflict session 2 saves the title "Session 2 finally"
    Then conflict note 2's stored title is "Session 2 finally"

  # ── (4) the generic form's coarse banner: Keep mine force-commits over theirs ─────────────────────────
  # Same setup. Session 2 chooses Keep mine → its value is force-committed at the current base, overwriting
  # session 1's value (chosen consent); the store then holds session 2's value and the in-form bar clears
  # (the generic form never raised the global "reload" banner — see scenario 3; that banner is the
  # custom-render fallback only).
  @multi-user
  Scenario: Keep mine force-commits the draft over the other session's value
    Given the conflict fixture app is served
    And conflict session 1 opens the note at "/notes/2"
    And conflict session 2 opens the note at "/notes/2"
    When conflict session 1 saves the title "Session 1 first"
    Then conflict session 1's save lands in the store
    When conflict session 2 saves the title "Session 2 forces"
    Then conflict session 2 sees the conflict banner naming "title"
    And conflict session 2's Save button is hidden while the bar shows
    When conflict session 2 clicks Keep mine
    Then conflict note 2's stored title is "Session 2 forces"
    And conflict session 2's conflict banner is gone
    And conflict session 2's global error banner is gone
    And conflict session 2's Save button is visible again

  # ── (B5-a) the FINE bar shows THEIRS (and yours) inline BEFORE the operator picks ─────────────────────
  # M13 Track-B B5 (the fine per-field UI). The coarse bar named the field but the operator chose BLIND —
  # the wire carried `theirs` but the client dropped it. B5 stops dropping it: the same-field collision now
  # renders session 1's value (theirs) AND session 2's own draft (mine) inline in the bar, so the operator
  # SEES both sides before choosing. (The headline obligation from the slice-6 ledger.)
  @multi-user
  Scenario: The fine conflict bar shows theirs and mine inline before the operator chooses
    Given the conflict fixture app is served
    And conflict session 1 opens the note at "/notes/2"
    And conflict session 2 opens the note at "/notes/2"
    When conflict session 1 saves the title "Their landed title"
    Then conflict note 2's stored title is "Their landed title"
    When conflict session 2 saves the title "My kept title"
    Then conflict session 2 sees the conflict banner naming "title"
    And conflict session 2's conflict bar shows theirs value "Their landed title"
    And conflict session 2's conflict bar shows mine value "My kept title"
    And conflict session 2's conflict bar group is labeled for note 2

  # ── (B5-b) per-field resolution: take-theirs one field, keep-mine another, in ONE object ──────────────
  # Session 2 edits BOTH of note 2's fields (title + count) and the same base was collided on both by
  # session 1. The fine bar lists two field rows in ONE object group. Session 2 resolves per-field: Take
  # theirs for `title`, Keep mine for `count`. Resolving the last field re-commits at the fresh base — the
  # store then holds title = THEIRS and count = MINE. (Per-field take-mine / take-theirs — obligation 3;
  # the shrink-ctx.conflicts model, no client-only picks collection.)
  @multi-user
  Scenario: Per-field resolution takes theirs for one field and keeps mine for another
    Given the conflict fixture app is served
    And conflict session 1 opens the note at "/notes/2"
    And conflict session 2 opens the note at "/notes/2"
    When conflict session 1 saves note 2's title "One's title" and count 7
    Then conflict note 2's stored title is "One's title"
    And conflict note 2's stored count is 7
    When conflict session 2 saves note 2's title "Two's title" and count 3
    Then conflict session 2 sees the conflict banner naming "title"
    And conflict session 2 sees the conflict banner naming "count"
    When conflict session 2 takes theirs for field "title"
    And conflict session 2 keeps mine for field "count"
    Then conflict note 2's stored title is "One's title"
    And conflict note 2's stored count is 3
    And conflict session 2's conflict banner is gone

  # ── (7) a CUSTOM render composing <ObjectForm> surfaces the conflict itself — no redundant global banner ─
  # A fully-custom fn render() that composes <ObjectForm> gets the fine <ConflictBar> on a conflict. This is
  # the general shape of the banner-suppression fix (B5 fast-follow): the global "reload" banner is the
  # no-silent-clobber FALLBACK, keyed on "did ANY render read ctx.conflicts (a resolver 'door')?" — NOT on
  # app type. A custom app opts out simply by HANDLING the conflict. Here the custom render surfaces it via
  # ObjectForm's ConflictBar, so the operator is NOT silently clobbered (the in-form bar names the field),
  # and the contradictory global "reload = discard your draft" banner is suppressed. (In fact a same-field
  # conflict can ONLY arise through a staging ctx, and only ObjectForm opens a conflict-carrying staging ctx
  # today — which is why it always surfaces via ConflictBar. The fallback branch stays for a conflict
  # surfaced by NO render, defensively; only clearly reachable via navigate-away-mid-reply.)
  @multi-user
  Scenario: A custom render composing ObjectForm surfaces the conflict itself, so no global reload banner fires
    Given the custom-render conflict fixture app is served
    And conflict session 1 opens the custom note page
    And conflict session 2 opens the custom note page
    When conflict session 1 saves the custom title "Custom one"
    Then conflict session 1's custom save lands in the store
    When conflict session 2 saves the custom title "Custom two"
    Then conflict session 2 sees the conflict banner naming "title"
    And conflict session 2's global error banner is gone
