@m12
Feature: Inline preview in the design editor (visual designer S3a, browser)
  End-to-end proof of the tree-as-data preview mechanism through a real browser against the real
  kernel-hosted designer: the design editor shows `sys.previewRender(design)` as REGULAR inline
  content (no iframe), rendering the design's own UI against its initialData seed. Crucially it
  SURVIVES CLIENT HYDRATION — the earlier tag-shipping variant blanked here because a tag has no
  wire form and the client recompute missed; shipping the render AS DATA and reviving it client-side
  keeps the preview after the client render replaces the DOM. The preview REFRESHES ON DEMAND (a
  Refresh button re-keys the memo) rather than auto-live per edit — auto-per-edit forced a server
  refetch on every design edit, which raced the designer's optimistic tree-editor mutations.

  @single-user
  Scenario: The design editor shows the design's real UI as an inline preview that survives hydration
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I edit the design "todo"
    Then the inline preview shows the design's rendered content "User 1"
    And the inline preview still shows "User 1" after a reload and hydration

  @single-user
  Scenario: Refreshing the preview picks up an edited seed
    Given the operator IDE is running on a kernel hosting instances "todo" and "crm"
    When I edit the design "todo"
    And I rename the design's seeded user to "Renamed User"
    Then the inline preview still shows the old content "User 1", proving the edit alone does not refresh it
    When I click Refresh on the preview
    Then the inline preview shows the design's rendered content "Renamed User"
