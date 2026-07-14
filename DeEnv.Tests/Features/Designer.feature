Feature: The operator IDE (designs library + instance design selector)
  The designer (instance 1) is a URL-routed multi-instance IDE, authored as an explicit custom
  `fn render()` over a `Db { designs: set of Design }` meta-schema. A `Design` is a WHOLE app —
  structured `types` plus the other app-document sections (initialData/common/ui) as source text. The
  surfaces are SEPARATE: `/designs` is the design LIBRARY (list + per-design edit/delete) and
  `/designs/<designId>` is the design EDITOR (type/prop editor + ui/common/initialData code areas, NO
  publish); `/instances` lists the hosted instances each showing its CURRENT design, and
  `/instances/<id>` is ONLY a design SELECTOR — a `<select>` dropdown of the designs with the
  instance's current one pre-selected + an Apply button that records the chosen design on the
  instance AND deploys it. The instance↔design link is an EXPLICIT reference: each instance stores a
  `designId` (the id of a design in the designer's `db.designs`), seeded so the dropdowns start
  correct and read back to pre-select. The seeded designs are FAITHFUL copies of the committed apps
  the kernel runs (todo, crm, shop). Driven against a REAL kernel host (the
  designer needs a non-empty `sys.instances`), through a browser. Milestone 10.
