# Generic UI — default UX spec (research-backed)

**Provenance.** Synthesized 2026-06-22 from a deep-research pass (27 sources → 25 claims
verified → **20 confirmed, 5 killed**) across the schema-driven generators (Django Admin,
Odoo, Directus, PocketBase) and the quality bar (Airtable, HubSpot/Salesforce record pages,
Cloudscape, Primer, NNG). The harness's auto-synthesis hit a usage cap, so this is the
hand-merged spec.

**Goal.** Reasonable, **schema-derivable** defaults that make deenv's auto-generated UI feel
close to hand-crafted apps, with **no per-app hand-tuning** (minimal by default).

**Legend.** 🟢 backed by a verified cited claim · 🔵 my recommendation/synthesis (convention,
not separately cited) · ⚠️ unverified or refuted in research — do **not** treat as fact.

---

## Core principle — the schema *is* the config

Every default is a pure function of the schema: `field type → control + display`,
`entity → list + detail + nav slot`, `reference → label-link + picker`, `collection → table`.
The zero-config path is the schema; deviation costs explicit Code. The generators confirm this
is the right spine:

- 🟢 **Django** derives the input widget per field type (`CharField→CharField`,
  `ForeignKey→ModelChoiceField` dropdown over the related queryset, `M2M→MultipleChoiceField`).
- 🟢 **Odoo** picks a default widget per field type; `widget=` override is the deviation.
- 🟢 **Directus** separates two derivations per field type: an **interface** (the edit input)
  and a **display** (the read-only render). deenv should do the same — a read display and an
  edit control per type. ⚠️ (Directus does *not* fully auto-pick the interface — it's selectable;
  so the honest rule is "derive a sensible default, allow override," not "pure auto.")

---

## 1. App shell & navigation

- **Pattern** 🔵 — a persistent **left sidebar of entities** + breadcrumbs + (later) global search.
  Universal across Django Admin, Odoo, PocketBase, Directus, Retool.
- **deenv today** — breadcrumbs + path-walk; **no sidebar**.
- **DEFAULT** 🔵 — auto-generate a left sidebar from `Db`'s top-level collections (the entity list),
  always visible; keep breadcrumbs mirroring the URL. Derivable: `Db`'s collection props = the items.
  Collapse/omit when there's ≤1 top-level collection.
- **Trade-off** — chrome cost; negligible for a real multi-entity app, the point at which it matters.

## 2. Collection / list views

- **Pattern** — dense table, smart default columns, row-click→detail, **New** top-right, column
  sort, pagination; multiple views (table/kanban/calendar) as an *upgrade*.
- **Generators** — 🟢 Django: no `list_display` → **one** column (the `__str__` label); otherwise
  all non-`AutoField`, `editable=True` fields in model order; **pagination defaults to 100/page**
  (show-all cap 200); a `ForeignKey` shows the related label; `ManyToMany` is **excluded** from
  list columns (a per-row query). 🟢 Odoo: one model → many view types (list/kanban/calendar/pivot)
  selected by `(model, type)`.
- **deenv today** — set table shows label + all non-collection scalars + **single refs as label**
  (just shipped); collections excluded; read-only; New *below*; no sort/pagination.
- **DEFAULT** 🔵 (grounded) — columns = **label first + scalars + single-refs-as-label** (deenv
  already does this — strictly better than Django's 1-column default), **excluding collections**
  (matches Django dropping M2M). Add **column sort** and **pagination** (default ~50/page; Django's
  100 is the upper anchor). Row-click → detail. Move **New to top-right** (convention). Multi-view
  (kanban/calendar) is **deferred** — but the model supports deriving it (Odoo proves one model →
  many views), so don't foreclose it.
- **Trade-off** — all-scalars can make wide tables; cap default columns to the first N, rest on the
  detail page.

## 3. Record / detail pages — *the biggest lever*

- **Pattern** — a record **HEADER** (title + key actions + status), fields grouped into **SECTIONS**,
  and **related records as "related lists."** This is where every quality-bar app converges:
  - 🟢 **Airtable**: title defaults to the **primary field**; fields organized into configurable
    **Groups/sections** (tabs, collapsible), not a flat list; linked records are clickable and
    link/unlink inline.
  - 🟢 **HubSpot**: a fixed **3-column** record layout — left "highlight" (primary+secondary display
    props, e.g. name/email, *are* the header), middle (activity), **right sidebar = Associations**
    (related records grouped onto cards by object type). This is the CRM **related-lists** convention.
  - 🟢 **Odoo**: form `<header>` = workflow buttons + **statusbar**; `<group>` = 2-column field layout.
- **deenv today** — a **flat field list**. No header, no sections, no related lists.
- **DEFAULT** 🔵 (grounded) —
  (a) **Header**: the type's `labelProp` value as the title + primary actions (Delete, …) + the
  status if the type has an enum status field (Odoo statusbar).
  (b) **Sections**: scalars in a 2-column group (Odoo default); each **single nested object** as its
  own labeled section (deenv already inlines these — just title them).
  (c) **Related lists**: render the object's **sets/dicts as related-record tables below the fields**
  (deenv already inlines sets as tables — formalize them as titled "related lists"). Reverse-relations
  (other entities pointing *at* this record) are a **deferred** related-list (needs a reverse-ref index).
- **Trade-off** — sectioning heuristics can mis-group; keep the rule simple (one scalar group + one
  section per nested object + related-lists for collections).

## 4. Relations / references

- **Pattern** — show a reference as a human **label that LINKS** to the related record; edit via a
  **searchable picker**; surface reverse-relations as related lists.
  - 🟢 Django: a `ForeignKey` renders as the related label (not a raw id); editing a FK = a
    `ModelChoiceField` (dropdown over the related queryset).
  - 🟢 Airtable: linked records are clickable + link/unlink inline.
- **deenv today** — ref = **label link** (just shipped ✅); picker = a plain `<select>`; null = blank.
- **DEFAULT** 🔵 (grounded) — ref = referent's `labelProp` as a **link** (deenv ✅). Upgrade the
  `<select>` to a **searchable typeahead** (a plain select stops scaling past ~50 options). Reverse-
  relation lists = **deferred**. ⚠️ Airtable's "links are bidirectional by default" was *not* verified
  — treat reverse-lists as an explicit feature, not free.
- **Trade-off** — deenv's current `<select>` is fine for small extents; flag the searchable upgrade.

## 5. Editing model — *research validates deenv's existing choice*

- **Pattern** — the verified guidance **defaults to EXPLICIT SAVE for forms**; inline/autosave is
  reserved for frequently-edited grids and imperative controls.
  - 🟢 **Primer**: "start with an explicit saving pattern" for forms; autosave only for imperative
    controls (toggles, segmented, single-select) where instant feedback is expected.
  - 🟢 **Cloudscape**: inline edit for frequently-updated views / editing one property across many
    rows; **page-edit** for editing one item's properties; inline edit uses an **explicit per-cell
    confirm**, not silent autosave.
  - ⚠️ Airtable's "3 edit modes (off/inline/form)" and "inline = autosave" were *not* verified.
- **deenv today** — object forms **stage + Save** (autosave off by default); tables **read-only**
  (edit on the detail page); toggles/ref-pickers autosave via `field`/`setRef`.
- **DEFAULT** 🟢 (validated) — **keep deenv's current model.** It *matches* the verified guidance:
  explicit Save for object forms (Primer), read-only tables → edit-on-detail (Cloudscape page-edit),
  autosave only for the imperative controls. This is the research's clearest result — the earlier
  "tables are read-only" decision is the conventionally-correct default, **not** a gap.
- **Trade-off** — inline-grid editing (the Airtable feel) is a *deferred opt-in* for frequently-edited
  collections, not the default.

## 6. Actions

- **Pattern** 🔵 — primary **Create** top-right of a list; per-row actions in a trailing column or a
  **⋯ overflow** menu; record actions in the header; bulk actions on multi-select; **confirm**
  destructive actions.
- **deenv today** — New (below — move up); per-row Remove (trailing) ✅; designer **kebab** ✅;
  **Delete confirm** ✅ (just shipped); no bulk multi-select; no record-header actions yet.
- **DEFAULT** 🔵 — New top-right of each table; single trailing action stays a button, escalate to a
  **⋯ kebab once >2 actions** (designer ✅); record-header Delete (+ type actions) once §3 lands;
  destructive = 2-step confirm (✅); **bulk multi-select deferred**.

## 7. Find — search / filter / sort / paginate

- **Pattern** — per-collection search, column sort, column filters, pagination.
  - 🟢 Django: **pagination default 100/page** (cap 200) — a concrete derivable default.
- **deenv today** — a filter *expression* exists (M6) but little UI; **no search/sort/pagination UI**.
- **DEFAULT** 🔵 (grounded) — (a) **pagination on by default** (~50/page; Django's 100 is the ceiling)
  — derivable, zero-config, and the #1 fix for the "toy" feel of an unbounded table; (b) **column sort**
  (click header) — derivable from scalar columns; (c) a per-collection **search box** over the label +
  text columns; (d) column filters = later.
- **Trade-off** — pagination is cheap + essential; sort/search add interaction code but are expected.

## 8. States & polish

- **Pattern** — meaningful empty states, loading states, sane density, clear type hierarchy.
  - 🟢 **NNG**: empty states are a first-class design surface — an empty collection should explain
    itself and offer the primary (New) action, not show a bare header.
- **deenv today** — a basic default stylesheet; empty tables show a header + New (no explanatory empty
  state); basic density.
- **DEFAULT** 🔵 (grounded) — (a) empty collection → "No `<Things>` yet" + the New action; (b) a
  consistent row density + spacing + type hierarchy in the default theme; (c) loading: SSR-first means
  little spinner need — deenv's structural strength.

---

## Cross-cutting — why auto-UIs feel "generated," and the fixes

What reads as **generated/cheap** (Django-Admin tier): a 1-column list (just `__str__`), a flat
undifferentiated field dump, raw ids, no record header, no entity sidebar, no empty states, default
browser controls. What makes **Odoo/Directus/Airtable acceptable**: a per-type control *and* display
(Directus's interface/display split), a real **record header + sections + related lists**
(Airtable/HubSpot/Odoo), **label-links not ids** (Django FK `__str__`), and **many views from one
model** (Odoo).

**Highest-leverage gap-closers for deenv:**

1. **Record page: header + grouped sections + related lists (§3)** — the single biggest
   "designed vs. dumped" lever; every quality-bar app converges here.
2. **App-shell entity sidebar (§1)** — the "you're in a real app" frame.
3. **Pagination + sort + search (§7)** — unbounded raw tables read as a toy.
4. **Empty states + density/type polish (§8).**

**Already landed / validated — keep:** refs-as-label-link (§4 ✅), the explicit-Save-forms +
autosave-controls editing model (§5 — research *validates* this), kebab + confirm actions (§6 ✅).

## Prioritized roadmap

- **P1 — Record page (§3):** header + sections + related lists. Highest feel-per-effort; converged
  across all references.
- **P2 — App shell (§1):** the entity sidebar.
- **P3 — Collections (§2, §7):** pagination + column sort + search; New top-right.
- **P4 — Polish (§8):** empty states + density/theme.

> Editing-model caveat: the research's clearest finding is that deenv's **existing** editing model is
> the conventionally-correct default — do **not** switch to inline-grid-autosave as the default; it's a
> later opt-in for frequently-edited collections.

---

## Sources (verified)

- Django Admin — https://docs.djangoproject.com/en/5.0/ref/contrib/admin/ (columns/`__str__`,
  pagination 100/200, FK label, M2M excluded)
- Django ModelForms — https://docs.djangoproject.com/en/3.2/topics/forms/modelforms/ (field-type →
  form-field; FK → ModelChoiceField)
- Odoo views — https://www.odoo.com/documentation/15.0/developer/reference/backend/views.html and
  .../19.0/.../view_architectures.html (multi-view from one model; `<header>`/`<group>`/statusbar;
  per-type default widget)
- Directus fields — https://directus.io/docs/guides/data-model/fields (interface vs display)
- Airtable record detail — https://support.airtable.com/docs/airtable-interface-layout-record-detail
  (primary-field title; Groups/sections; linked records inline)
- HubSpot record layout — https://knowledge.hubspot.com/records/understand-the-default-record-layout
  (3-column layout; highlight header; Associations/related lists)
- Cloudscape inline-edit — https://cloudscape.design/patterns/resource-management/edit/inline-edit/
- Primer saving — https://primer.style/ui-patterns/saving/
- NNG empty states — https://www.nngroup.com/articles/empty-state-interface-design/

_Refuted/unverified (do not assert): Directus auto-derives the interface from type (0-3); Odoo
`<notebook>`/`<page>` tabbed sectioning as the mechanism (1-2); Primer prohibits mixing save patterns
(abstain); Airtable links bidirectional by default (abstain); Airtable's three edit modes (abstain)._
