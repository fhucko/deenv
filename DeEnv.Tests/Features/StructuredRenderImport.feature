@m12
Feature: Import a text-authored render into structured rows (M12 S1b)
  A design authored as `ui` TEXT (a custom `fn render()`) is imported INTO the structured
  MetaNode tree (Design.render) — the inverse of S1a's projection. The walk mirrors the
  tree: each element becomes a MetaNode {tag}, each attribute a MetaAttr, each child a
  MetaNode (a leaf carries its expression as text). Import then CLEARS the `ui` text field,
  so the S1a precedence gate accepts the structured render as the authority. The proof is a
  lossless round-trip: ProjectDesignDocument(after import) equals the canonical form of the
  original render — import then project is the identity on the render (modulo formatting).
  Server-side store logic only: no interpreter/grammar/conformance change, no wire, no UI.

  Scenario: A text-authored render imports to structured rows and projects back unchanged
    Given a design whose `ui` text is a fn render() returning <main class="hello"> containing an <h1> whose child is the text "Hi"
    When the design's render is imported to structured rows
    Then the design's `ui` text field is empty
    And the design's `render` set holds the structured tree
    And projecting the design yields a `ui` section equal to the canonical form of the original render

  Scenario: Import refuses a render that uses a foreach form
    Given a design whose `ui` text is a fn render() whose body iterates a set with a foreach
    When the design's render is imported to structured rows
    Then the import fails with a schema validation error
    And the design's `render` set is still empty
    And the design's `ui` text field is unchanged

  Scenario: Import refuses a design that already has a structured render tree
    Given a design whose `ui` text is a fn render() returning <main class="hello"> containing an <h1> whose child is the text "Hi"
    And the design's render has already been imported to structured rows
    When the design's render is imported to structured rows
    Then the import fails with a schema validation error
