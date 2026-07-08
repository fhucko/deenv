@m12
Feature: Structured render tree → running fn render() (visual designer S1a)
  The visual designer stores render code as ordinary structured data — a MetaNode tag tree
  owned by Design.render — and projects it to the canonical `fn render()` text, the same
  authority-inversion the `types` set already uses (structure = truth, printed text = artifact).
  Execution is unchanged: the projected text flows through the existing parse→run pipeline, so a
  structured render renders exactly as the equivalent hand-written custom UI would. S1a is plain
  tags + attrs + text/expr leaves only — no for…in / if, no import, no canvas.

  Scenario: A render tree stored as structured rows projects to a running fn render()
    Given a design whose `ui` text is empty
    And the design's structured render is a `main` element with class "hello" containing an `h1` whose child is the text "Hi"
    When the design is projected to an app document
    Then the document has a `ui` section containing `fn render()`
    And loading that document and rendering it produces HTML with an <h1> reading "Hi" inside a <main class="hello">
