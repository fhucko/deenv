# Component library — plan (the "how" to the catalog's "what")

The plan for deenv's **public component library** — the proper, consistent set of components needed to
build the doc'd apps (ERP / eshop / accounting / CRM / admin / dashboards). This is the **feature half
of M11** (the mechanism + first primitives `Input`/`Field` + the public `lib` scope have landed); this
doc scopes the *full set*, the *consistency conventions* ("same style"), and the *build strategy*. Pairs
with `docs/ui-workflow-catalog.md` (the *what* — building blocks) as the *how*. **Plan only — built
emergently, app-driven, not in one sprint.** *2026-06-18.*

## Is it a milestone?
It's **M11's feature half**, not a standalone milestone — and the *comprehensive* set is an **emergent,
app-driven track**, not a finish-line. M11 delivers the library *capability* + an initial set; the full
set fills out **slice-by-slice as the example apps need it**, measured against the catalog as the
done-checklist. Don't speculatively build the whole set; extract components from real app usage, in the
same style.

## 1. Scope — the component families
(From `ui-workflow-catalog`; each built when an app first needs it.)
- **Primitives:** Input, Field, Button, Select, Checkbox/Toggle, Textarea, DatePicker, Combobox.
- **Data display:** **Table** (+ toolbar / filter / sort / column-config / selection / inline-edit /
  row-actions / totals — the big one), List, Badge/Status, Card, KPI/Stat, EmptyState.
- **Forms:** Form, Section/Tabs, **DocumentForm** (header + line-items grid + totals), ReferencePicker
  (pick + create-inline), validation/error display, conditional fields, Wizard.
- **Layout / shell:** Page, PageHeader (+ actions), **Toolbar**, **SideNav** (module menu), Breadcrumb,
  Tabs, Split, Modal, Drawer.
- **Feedback:** Toast, Loading/Skeleton, Confirm.
- **Later / heavy (M12+ / future):** full data grid (grouping / pivot / virtualization), window manager
  (tabs → split → docking), the visual workflow builder.

## 2. "Same style" — the consistency contract
A *proper* library is consistent on two axes, not one:
- **Visual → shared semantic tokens.** Every component reads the design tokens (the themes work:
  two-layer semantic CSS vars, compact/dense default). No component hard-codes color / spacing / type.
  One look; themeable in one place.
- **API → shared authoring conventions** (the style guide):
  - **Data in via `of={…}`** (the object/value rendered); config via named props (`label`, `density`,
    `columns`, …).
  - **Composition + slots** — accept children / named slots for overrides
    (`<ObjectForm of={x}>{ override a field }</ObjectForm>`), not a wall of props.
  - **`<Field>`-shaped** primitives (label + control + error in one; static inset label by default).
  - **Density-aware** — every component honors the `density` token (compact default).
  - **SolidJS-style reactivity** — setup once, reactive view; reset = position/key (the M11 foundation).

## 3. Build strategy — emergent, app-driven
- **Apps drive the library.** Build each example app (ERP → eshop → accounting → CRM); when it needs a
  component, build it *in the app*, then **extract** the reusable part into the shared `lib` in the same
  style. The library *emerges* from real usage, never designed in a vacuum.
- **Consumers = the proof.** The **generic UI** (the first consumer) + the **example apps** are both the
  showcase *and* the regression suite — a component that breaks them is caught.
- **Slice per family.** Each slice = one component (or a tight family) + its Gherkin + (twin-relevant)
  conformance, extracted and themed. The catalog is the backlog; tick items off as apps land them.
- **Generic-UI-as-first-consumer collapse** (M11 remaining): the generic UI is rewritten to compose the
  public components — the completeness proof (if the library can build the whole generic UI, it's
  complete + first-class).

## 4. Sequencing
- **M11 (in flight):** mechanism ✓ + `Input`/`Field` ✓; remaining — publish more primitives, the
  operator designer as a 2nd consumer, the generic-UI-as-first-consumer collapse. The families the first
  apps + the generic UI need land here.
- **Then (ongoing):** fill out the families per example app, against the catalog.
- **Later (M12+ / future):** the heavy components above.

## The honest framing
The library is a **track, not a milestone with a finish line.** "Done" = "the catalog is covered,"
reached by **building the example apps**, not by a component-design sprint. M11 makes it *possible* and
*consistent*; the apps make it *complete*.
