# UI & workflow building-block catalog

A catalog of the **interaction + process building blocks** needed to build deenv's target apps
(ERP, eshop, accounting, CRM, admin, dashboards — see `docs/domain/`). It complements the **data**
reference docs (which catalog *entities*) with the **UI/workflow toolkit**. It doubles as **(a)** the
spec for the M11+ public component library and **(b)** the seed for future per-item "how to add it"
docs. **Living list — still being filled out** (K2 ERP is the reference for the power-user / workflow
end). *Recorded 2026-06-18 from a design discussion.*

**Each item is tagged by *what builds it in deenv*:** `[data]` schema / object model · `[component]`
the component library · `[code]` object Code (guards / actions / render) · `[builder]` a future visual
builder/tool · `[pillar]` a deferred pillar (auth / real-time / versioning / storage).

**Scope:** ✅ expressible today · 🔜 component-era (builds on M11) · 🟣 future tool/pillar.

> The point isn't the *look* (that's the token/theme layer) — it's the **capabilities that enable
> real workflows.** A button/control bar above a table, a side menu + content, a multi-record
> workspace, an approval flow: these are what make the apps usable, not the styling.

## Layer 1 — App shell / layout
- Top bar — app title, global search, user menu, notifications. 🔜 `[component/code]`
- **Side-nav module menu** (the ERP module tree). 🔜 `[component/code]`
- Breadcrumbs — exists in the generic UI. ✅ `[component]`
- Page header + page-level actions. 🔜 `[component]`
- Content layouts: list · detail · dashboard · **master-detail split**. 🔜 `[component/code]`
- **Workspace / window management (tabs → split-panes → docking).** 🟣 `[builder/code]` — the
  K2/power-user multi-record experience (a customer + their order + inventory open at once).
  **Load-bearing rule:** each window/panel stays **URL-addressable** (a panel shows a node by its URL →
  deep-linking, graph nav, breadcrumbs survive *inside* it); a **workspace shell** (custom render)
  arranges multiple panels; only the *layout* is app state (optionally serialized → restorable
  "workspace"). Default stays single-page; the window manager is an **opt-in shell**. **First consumer =
  the IDE** (the unified designer + data + preview + versioning workspace, pillar 9). Minimum = tabs;
  ceiling = MDI/docking. *Tension recorded: a window manager is workspace state, not a single URL walk —
  the URL-addressable-panels rule is how it reconciles with URL-as-navigation.*

## Layer 2 — Table & form affordances
**Table** (the "control/button bar above the table" + more):
- Toolbar — New / Import / Export + **bulk actions** on selected. 🔜 `[component/code]`
- Filter bar / quick search / **saved views**. 🔜 `[component]`
- Sort, pagination, **column config** (show/hide/reorder) + density toggle. 🔜 `[component]`
- Row selection, inline edit, row `…` actions, row → detail. 🔜 `[component]`
- Grouping + **aggregation/totals**; pivot (🟣). 🔜 `[component]`
- Export (CSV / Excel / PDF), print. 🔜 `[component/code]`
- Empty / loading states. 🔜 `[component]`

**Form:**
- Sections / **tabs** (deep records → the auto-tabs idea, pillar 8). 🔜 `[component]`
- The **document form** (header + line-items grid + running totals) — the ERP/invoice shape. 🔜 `[component/code]`
- Validation + error display; **conditional fields** (show B if A = x). 🔜 `[code]`
- Reference lookups (pick existing + create-inline). ✅ `[component]`
- Form actions — Save / Save & New / Cancel **+ workflow actions (Submit / Approve / Reject)**. 🔜 `[code]`
- Wizards / multi-step. 🔜 `[component/code]`

## Layer 3 — Status & process (the workflow layer)
*Where K2 shines; the bigger half.*
- **Status / lifecycle** on a record (the status enum: lead → quoted → … → paid). ✅ `[data]` (enum type done)
- **Guarded transitions** — Approve only if Submitted; who's allowed. ✅ basic / 🟣 perms `[code / pillar]`
- **Actions on transition** — on approve → post to GL, notify. ✅ `[code]`
- **Approval routing** — who approves, order, approve / reject / **delegate**, **approval history**. 🟣 `[code/builder/pillar]`
- **Visual workflow BUILDER** — define states + transitions + roles + actions, **embeddable anywhere in
  the app**. 🟣 `[builder]` — the big future piece: a *visual process designer*, the **process cousin of
  the visual component designer (M12)**. The *basics* (status + transitions + actions) are expressible
  today in data + Code; the **builder** is the future tool.

## Layer 4 — Cross-cutting
- **Permissions / visibility** — who sees / does what. 🟣 `[pillar: auth]` — *workflows need this hard
  ("who can approve"); it is the flagged auth gap, the biggest under-analyzed dependency.*
- **Notifications / task inbox** — "needs your approval." 🟣 `[pillar: real-time]`
- **Audit trail / history** — who-did-what-when. 🟣 `[pillar: versioning / temporal]`
- Reports / dashboards; bulk import/export; **document generation** (PDF invoices / POs). 🔜 / 🟣 `[component/code]`

## How this sequences
- **Layers 1–2 = component-era** — build on M11 (the public component library). This catalog *is* the
  spec for what that library must cover.
- **Layer 3 basics = ✅ today** (status enum + Code guards/actions). The **workflow builder = a future tool.**
- **Layer 4 = mostly deferred pillars** — **auth first** (it gates workflows *and* multi-user apps).
- The two big future *tools* in here are the **window manager** and the **workflow builder** — both
  embeddable shells/designers, pillar-9 / IDE-era, and both reconcile with deenv's model rather than
  replace it (URL-addressable panels; status-as-data + Code).
