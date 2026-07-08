@m12
Feature: Inline live preview of a design (visual designer S3a)
  The designer shows a live preview of the design being edited as REGULAR CONTENT (no iframe, no
  kernel mount), via the server-backed read `sys.previewRender(design)`. The server renders the
  design HEADLESSLY — its own render over a throwaway store seeded from the design's initialData —
  and ships the result AS PLAIN DATA (a handler-stripped {tag, attrs, children}/text graph, which
  HAS a wire form, unlike a tag tree). At the call site both twins revive that data into a real tag
  tree spliced inline. This drives the server compute + revival directly (no browser): the data must
  revive to the design's real structure against its seed, strip handlers, fail soft on an invalid
  design, and leave no temp files.

  Scenario: previewRender returns the design's rendered UI as revivable data
    Given a preview design whose render shows the seeded greeting inside a main.hello > h1 and a button with an onClick handler
    And its initialData seeds the greeting "Hello seed"
    When the design's preview is computed
    Then the preview revives to a <main class="hello"> whose <h1> text is "Hello seed"
    And the revived <button> has no onClick handler attribute
    And no preview temp files are left behind

  Scenario: An invalid design previews as a fail-soft error, not a crash
    Given a preview design that does not project to a valid app document
    When the design's preview is computed
    Then the preview revives to a <div class="preview-error"> without throwing
