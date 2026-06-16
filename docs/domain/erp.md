# ERP — domain reference

A working model of how a generic ERP is structured, written for building **deenv
templates** from. The lens is the **data model**, not screens: master data,
documents, line items, status lifecycles, computed values, and the *posting links*
that integrate them. Every pattern maps onto deenv's Stage-1 primitives — objects
with identity, references, sets, the association (line-item) object, status enums,
computed values, dictionaries — flagged inline as *deenv:* and summarised in §13.

> **Scope.** The universal core every ERP shares (Odoo / SAP / Dynamics / NetSuite
> differ in depth and packaging, not in this skeleton). Tax localization,
> accounting-standard edge cases (GAAP/IFRS), and industry-specific workflows are
> *named where they live* but not exhausted — they are the depth to add later
> (a deep-research pass per module is the tool for that).

---

## 1. The thesis — one integrated model

An ERP is a suite of modules over **one database**, so a single business event
ripples through every relevant area automatically: confirming a sales order
reserves stock; shipping it decrements inventory **and** posts a cost-of-goods
journal; invoicing posts a receivable; payment clears it. The value is the
**integration** (the posting links in §2.6), not any single module. Learn the core
pattern once and every module is a variation of it.

## 2. The six structural patterns (the skeleton)

1. **Master data** — persistent reference entities (the *nouns*): partners,
   products, accounts, warehouses. Edited occasionally, referenced constantly.
   *deenv: objects with identity, held in sets, referenced from documents.*
2. **Transactional documents** — dated business events (the *verbs*): orders,
   invoices, payments, receipts, journal entries. They reference master data and
   carry a status. *deenv: objects in sets, each with a `date`, a `status` enum,
   and references.*
3. **Header + lines** — a document is a header (who / when / status) plus a set of
   **line items**, each a *quantity × item × price* sitting **on** a relationship.
   The most recurring non-trivial ERP shape. *deenv: the association object — a set
   of line objects, each referencing an item and holding quantity + price.*
4. **Status lifecycle** — every document flows through states that gate what may
   happen (can't ship a draft; can't pay an unposted invoice). *deenv: a status
   enum + guarded transitions in object code.*
5. **Computed values** — derived, never stored as truth: line total = qty × price
   (− discount + tax); document total = Σ lines; stock on hand = Σ moves; account
   balance = Σ postings. *deenv: computed values (pillar 2, object code — no server
   calls in user code).*
6. **Posting / integration links** — validating a document *posts* effects into
   other modules: a stock move, a GL journal entry, an AR/AP entry. **This is the
   "integrated" in ERP.** *deenv: actions that create/reference objects in other
   sets.*

---

## 3. Master data (the shared foundation)

**Partner** — Customer / Supplier / Contact, usually one entity wearing roles.
- Fields: name, kind (company/person), tax id, addresses (billing + shipping),
  email/phone, **payment terms**, price list, currency, default AR/AP accounts,
  credit limit, active flag.
- *deenv:* object in a `partners` set; addresses an inline set; roles a status/flags.

**Product / Item**
- Fields: code (SKU), name, **type** (stockable / service / consumable), **unit of
  measure**, sales price, **cost**, tax category, income + expense (COGS) accounts,
  barcode, category, reorder point / min stock.
- Advanced: variants (size/colour), multiple UoM, bill-of-materials (§8).
- *deenv:* object in a `products` set; category a reference or dictionary key.

**Account (Chart of Accounts)** — the accounting backbone (§7).
- Fields: code, name, **type** (asset / liability / equity / income / expense),
  parent (grouping tree), currency, reconcilable flag.
- *deenv:* objects in an `accounts` set; parent a self-reference (tree).

**Supporting master entities** (small, referenced everywhere) — *deenv: mostly
dictionaries keyed by code, or small sets:*
- **Warehouse / Location** (stock lives at a location).
- **Unit of Measure** (+ conversions).
- **Tax code / rate** (VAT/sales-tax %, the accounts it posts to).
- **Currency** (+ exchange rates over time).
- **Payment term** (net-30, 50/50, etc. → due-date computation).
- **Number sequence** (per-document-type counters: INV/2026/0001).
- **Price list / discount rules** (advanced).

---

## 4. Sales — the Order-to-Cash (O2C) flow

The spine: **Quotation → Sales Order → Delivery → Customer Invoice → Receipt**,
posting to inventory and the GL along the way.

**Quotation / Sales Order** (same entity, status separates them)
- Header: customer (ref), order date, salesperson, currency, payment term,
  ship-to, status.
- **Lines** (set): product (ref), description, quantity, unit price, discount %,
  tax (ref), **line total** (computed). *The association object.*
- Computed: untaxed total, tax total, **grand total**; delivered/invoiced
  quantities.
- **Lifecycle:** `draft (quote) → confirmed (order) → partially/fully delivered →
  invoiced → done`; plus `cancelled`.
- **Posting on confirm:** reserve stock; nothing hits the GL yet (an order is a
  commitment, not a transaction).

**Delivery / Shipment**
- Header: source order (ref), customer, date, ship-from warehouse, status.
- Lines: product, quantity shipped (≤ ordered).
- **Posting on validate:** **stock move out** (on-hand ↓); under perpetual
  inventory, **DR Cost-of-Goods-Sold / CR Inventory** at item cost.
- Lifecycle: `draft → ready → done`; supports back-orders (partial).

**Customer Invoice**
- Header: customer, invoice date, **due date** (= date + payment term), source
  order/delivery (ref), currency, status.
- Lines: product/description, qty, unit price, tax → **line + tax totals**.
- **Posting on validate (double-entry):** **DR Accounts Receivable** (grand total),
  **CR Revenue** (untaxed), **CR Tax Payable** (tax). Creates an **AR open item**.
- Lifecycle: `draft → posted → paid / partially paid`; `cancelled`; **credit
  note** (return/refund) reverses it.

**Customer Payment / Receipt**
- Fields: customer, date, amount, bank/cash account, allocated invoices (refs).
- **Posting:** **DR Bank/Cash / CR Accounts Receivable**; clears the AR open item.
- Handles partial payments, over-payments (credit on account), and **allocation**
  (one payment across many invoices).

**Returns / Credit Notes** — reverse delivery (stock back) and/or invoice (AR down).

**Features:** quotations + send/print, order confirmation, partial/back-order
delivery, multiple price lists + discounts, taxes, multi-currency, customer credit
limit check, invoicing from order or delivery, payment allocation, credit
notes/returns, aged-receivables report, sales reporting (by product/customer/rep).

---

## 5. Purchasing — the Procure-to-Pay (P2P) flow

The mirror of sales: **Requisition → Purchase Order → Goods Receipt → Vendor Bill →
Payment.**

**Purchase Requisition** (optional internal request) → approval → PO.

**Purchase Order**
- Header: supplier (ref), order date, expected date, warehouse, currency, status.
- Lines: product, quantity, unit cost, tax, **line total** (computed).
- Lifecycle: `draft (RFQ) → confirmed → received → billed → done`; `cancelled`.
- **Posting on confirm:** a commitment; no GL impact yet.

**Goods Receipt**
- Lines: product, quantity received (≤ ordered).
- **Posting on validate:** **stock move in** (on-hand ↑); **DR Inventory / CR
  Goods-Received-Not-Invoiced** (a clearing liability) under perpetual inventory.

**Vendor Bill** (supplier invoice)
- Header: supplier, bill date, due date, source PO/receipt (ref).
- **Posting on validate:** **DR Inventory-clearing/Expense + Tax / CR Accounts
  Payable**. Creates an **AP open item**.

**Vendor Payment** — **DR Accounts Payable / CR Bank**; clears the AP item.

**Three-way match** (PO ↔ receipt ↔ bill) is the control that quantities and prices
agree before paying. **Debit notes** reverse a bill.

**Features:** RFQ/quotes from vendors, PO approval workflow, partial receipts,
three-way matching, landed costs (freight into item cost), vendor price lists,
multi-currency, aged-payables report, purchase reporting.

---

## 6. Inventory / Warehouse

Tracks **what stock exists, where, and at what value** — driven entirely by stock
moves from sales, purchasing, and manufacturing.

**Item stock (Quant)** — on-hand quantity of a product at a location. *Derived from
moves, not edited directly.*

**Stock Move** — the atomic event: product, quantity, from-location, to-location,
date, source document. **On-hand = Σ moves.** Receipts move *in*, deliveries move
*out*, transfers move *between*, adjustments correct.

**Internal Transfer** — move stock between warehouses/locations.

**Inventory Adjustment / Stock Count** — physically count, post the difference as a
move (DR/CR an inventory-adjustment account).

**Valuation** (how much stock is *worth* — feeds COGS and the balance sheet):
- **Standard cost** (fixed per item), **Average cost** (weighted moving average),
  **FIFO** (first-in-first-out layers). Choice affects COGS and margin.
- **Perpetual** (every move posts to the GL — assumed above) vs **periodic** (GL
  updated at period end by a stock count).

**Advanced:** lots / serial numbers, expiry dates, multi-step routes
(pick→pack→ship), reordering rules / min-max, reservations, barcode operations.

**Features:** real-time on-hand by location, stock moves history, transfers,
adjustments/counts, valuation method, reorder rules, lot/serial tracking,
stock-valuation + movement reports, low-stock alerts.

---

## 7. Finance / Accounting — the backbone

Every module ultimately **posts** here. This is the single source of financial
truth, and it is its own deep domain.

**Double-entry bookkeeping.** Every transaction is a **Journal Entry** of balanced
lines: total **debits = total credits**. Each line hits one **account** for an
amount on the debit or credit side.
- *deenv:* a `journalEntries` set; each entry holds a set of lines (account ref,
  debit, credit); a computed invariant asserts Σdebit = Σcredit.

**Chart of Accounts** (§3) groups accounts into five types:
- **Assets** (cash, AR, inventory, fixed assets) · **Liabilities** (AP, tax
  payable, loans) · **Equity** (capital, retained earnings) · **Income** (revenue)
  · **Expenses** (COGS, salaries, rent).

**General Ledger (GL)** — all journal entries; **account balance = Σ its postings**.

**Sub-ledgers reconciled to control accounts:**
- **Accounts Receivable (AR)** — open customer invoices; aged buckets (0–30, 31–60,
  …); reconciles to the AR control account.
- **Accounts Payable (AP)** — open vendor bills; aged; reconciles to AP control.

**Bank / Cash + Reconciliation** — record receipts/payments; **reconcile** book
entries against the **bank statement** (match, find discrepancies).

**Tax** — transactions carry tax lines posting to tax-payable/receivable accounts;
periodic **tax return / VAT report** summarises tax collected vs paid. *Localization
(rates, rules, filing formats) is the deep, country-specific part.*

**Fixed Assets** (advanced) — capitalize an asset, **depreciate** it over its life
(periodic journal entries), dispose.

**Periods & closing** — the year is divided into **fiscal periods**; closing a
period locks it; **year-end close** rolls P&L into retained earnings.

**Core reports** (all computed from the GL):
- **Trial Balance** (every account's debit/credit balance — must balance).
- **Balance Sheet** (Assets = Liabilities + Equity, at a date).
- **Profit & Loss / Income Statement** (Income − Expenses, over a period).
- **Cash Flow**, **Aged AR/AP**, **General Ledger / account statements**, **Tax
  report**.

**Features:** chart of accounts, manual + auto journal entries, AR/AP sub-ledgers,
bank reconciliation, multi-currency (+ revaluation), tax codes + reporting, fiscal
periods + closing, fixed assets/depreciation, the standard financial statements,
audit trail (immutable posted entries), analytic/cost accounting (advanced).

---

## 8. Manufacturing (MRP) — for makers

- **Bill of Materials (BOM)** — a product's component list (component ref +
  quantity). *deenv: a set of component lines on the product — another association
  object.*
- **Manufacturing / Work Order** — consume components (stock out), produce the
  finished good (stock in); cost = Σ component costs + labour/overhead.
- **Work Center / Routing** — the operations + machines/time a product passes
  through. **MRP** plans purchases/production from demand vs stock. (Deep; only for
  manufacturing templates.)

## 9. CRM — the pre-sales pipeline

- **Lead** → **Opportunity** (with a pipeline **stage** status + expected value +
  close date) → converts into a customer + sales order.
- **Activity** (call/meeting/email/task) linked to a partner/opportunity.
- *deenv: an `opportunities` set with a stage enum (the lifecycle pattern again),
  referencing partners; activities a sub-set.* Note: CRM is the closest neighbour
  of the **freelance CRM + invoicing** target app.

## 10. HR (light) — people

- **Employee** (master data), **Department**, **Timesheet** (hours → project/cost),
  **Expense** (claim → reimburse → post). Full **payroll** (gross/net, tax,
  contributions) is a deep, localized domain usually built separately.

---

## 11. Cross-cutting concerns (every module needs these)

- **Number sequences** — human-readable per-type document numbers (SO/2026/0001).
- **Multi-currency** — store a currency + rate per document; revalue open AR/AP at
  period end.
- **Tax engine** — rate lookup by product × partner × jurisdiction; the posting
  accounts; the return.
- **Approval workflows** — thresholds that gate confirm/post (PO over X needs sign-
  off). *deenv: status + guarded transitions.*
- **Audit trail** — posted financial entries are **immutable**; corrections are new
  reversing entries, never edits. (Aligns with deenv pillar 4 — never-overwrite
  history.)
- **Reporting & dashboards** — almost all of it is **computed** from the documents
  + GL (totals, agings, statements), not separately stored.
- **Permissions / roles** — who may see/confirm/post what. *deenv: app-level
  (filter expressions over the object model) — a future pillar, not Stage 1.*

---

## 12. The deenv mapping (summary)

| ERP concept | deenv primitive |
|---|---|
| Master data (partner, product, account) | object with identity, in a set |
| Transactional document (order, invoice) | object in a set, with `date` + `status` enum |
| Document line item | **association object** — a set of line objects, each ref + qty + price |
| Status lifecycle | status enum + guarded transitions (object code) |
| Line/document totals, balances, on-hand | **computed values** (pillar 2) |
| Posting (invoice → GL, ship → stock move) | an action creating objects in another set |
| Reference data (UoM, tax, currency, terms) | dictionary (key = code) or small set |
| Chart-of-accounts / BOM tree | self-reference (parent) |
| Immutable posted entries | aligns with pillar 4 (temporal, never-overwrite) |

**This is why deenv fits ERP unusually well:** the Stage-1 capability bar (objects,
sets, references, the line-item association object, status enums, light computed
values, dictionaries — STAGES.md) is *exactly* the ERP construction kit. The
top-end stress cases there (the attendance grid; an invoice summing referenced time
entries) are literally ERP shapes.

---

## 13. Suggested template build order (smallest useful first)

Each is a thin, real slice — build them as deenv apps, share structure between them:

1. **Invoicing** — Partner + Product + Invoice(header + lines) + status + totals +
   AR. The smallest thing that touches the whole core pattern.
2. **+ Payments** — Receipt, AR allocation, aged receivables. Closes O2C.
3. **+ Sales orders & delivery** — order → delivery → invoice, with stock moves.
4. **+ Purchasing & inventory** — PO → receipt → bill → payment; on-hand from moves.
5. **+ Accounting backbone** — chart of accounts, journal entries, the financial
   statements (everything above starts *posting* to it).
6. **Specialize** — eshop (O2C + catalog/cart/fulfillment), accounting software
   (the §7 finance module standalone), CRM, manufacturing.

`eshop` and `accounting` are **subsets** of this document — eshop is the O2C slice
with a catalog/cart/fulfillment front-end; accounting software is §7 standalone.
~80% of each is already here.

---

## 14. Depth deliberately not covered here

Named so it doesn't masquerade as complete: tax **localization** (rates, rules,
e-filing per country), accounting **standards** (GAAP/IFRS recognition rules),
**multi-company/consolidation**, advanced **costing** (landed, analytic),
**payroll**, **MRP planning** depth, **WMS** (wave picking, etc.), and
industry-specific workflows. These are per-module deep dives — a deep-research pass
is the right tool when a template needs that rigor (accounting/tax first).
