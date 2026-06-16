# Accounting — domain reference

How accounting software works, written for building **deenv templates** — the
data-model lens (entities, fields, lifecycles, computed values, and the exact
**double-entry postings** per transaction). Accounting is the **finance module of an
ERP** ([erp.md](erp.md) §7) standing on its own; everything else in an ERP ultimately
*posts* here.

> **Provenance — read this.** This domain is correctness-critical (a template that
> does double-entry wrong corrupts books silently), so sources matter:
> - **[verified]** items were confirmed by a deep-research pass (3-vote adversarial
>   verification, 0 killed) against the sources in §15 — incl. **primary** sources:
>   IFRS Foundation, FASB ASU 2014-09, Microsoft Dynamics 365 docs, a US
>   Congressional Research Service report.
> - **[standard]** items are well-established accounting practice included for
>   completeness but **not independently verified in that pass** — treat as correct
>   textbook practice, but **cite/verify before shipping** a template that relies on
>   them. §14 lists what to research next.
>
> **Scope:** the universal, jurisdiction-agnostic core in depth; **tax rates,
> filing formats, and standard-specific rules are a localized variation layer**
> (§10, §11) — never hard-code one country's rules into the core.

---

## 1. The double-entry engine **[verified]**

Every transaction is a **journal entry** of balanced lines: **SUM(debits) =
SUM(credits)**. A debit is a transfer of value *to* an account, a credit *from* it
(debits left, credits right). The load-bearing invariant is the *sum* equality — an
entry has **≥ 2 lines**; compound/split entries (one debit, many credits, etc.) are
normal. Do **not** model "exactly one debit + one credit."
*Sources: Wikipedia (double-entry), Dynamics 365, Open University.*

- *deenv:* a `journalEntries` set; each entry holds a **set of lines** (account ref,
  debit amount, credit amount, dimensions); a **computed invariant** asserts
  Σdebit = Σcredit and *blocks posting* if unbalanced.

## 2. Accounts & the equation **[verified]**

Accounts are classified into **five types**, organized by the **accounting equation
Assets = Liabilities + Equity**, expanded to include income/expense:
`A − L = C + (I − E)`.

| Type | Normal balance | Increased by |
|---|---|---|
| **Asset** | Debit | Debit |
| **Expense** (and losses, dividends/drawings) | Debit | Debit |
| **Liability** | Credit | Credit |
| **Equity / Capital** | Credit | Credit |
| **Income / Revenue** (and gains) | Credit | Credit |

- **Income and expense are *temporary*** — not literally in A=L+E; they **close into
  equity** at period end (§8). Model 5 types; treat income/expense as equity
  sub-components that roll up at close.
- **Contra accounts** (accumulated depreciation, treasury stock) carry the
  **opposite** normal balance from their parent type.
- *Sources: Wikipedia, Open University, IFRS Conceptual Framework, FASB,
  double-entry-bookkeeping.com.*
- *deenv:* `accounts` set; fields: code, name, **type** (enum), **normalSide**
  (computed from type), parent (self-reference tree), `isContra`. **Validate every
  posting's direction against the account's normal side.**

## 3. The architecture — subledgers post, the GL is *derived* **[verified]**

The single most useful architectural fact for a template:

- Source **documents** (invoice, bill, payment…) post into a **subledger / module**
  — Accounts Receivable, Accounts Payable, Inventory, Fixed Assets, Tax. One
  document can hit **several** subledgers at once (a customer invoice → AR balance +
  Tax + COGS/Inventory).
- The **GL debit/credit lines are NOT entered on the document** — they are
  **generated from the subledger entries by configurable posting rules** that map to
  chart-of-accounts main accounts (+ dimensional coding). This is universal: SAP =
  *reconciliation accounts + account determination*; Oracle = *Subledger
  Accounting*; Dynamics = *posting profiles*. (Those names are vendor-specific — the
  *pattern* is universal.)
- *Source: Microsoft Dynamics 365 (cross-checked vs SAP, Oracle).*
- *deenv:* a document is an **object**; **posting is an action** that *derives* GL
  journal lines from the document via rules (account = main account + dimensions).
  This is the same "derive from authoritative source" move as deenv's memo cache —
  the GL is a projection of the documents, not hand-keyed.

## 4. Canonical postings (the journal entries)

The debit/credit entry per transaction. **[verified]** rows are research-confirmed;
**[standard]** rows are textbook practice to verify before shipping.

| Transaction | Dr / Cr | |
|---|---|---|
| **Customer invoice** (credit sale) | **Dr** Accounts Receivable / **Cr** Sales Revenue (+ **Cr** Output Tax for the tax part) | [verified] (tax line [standard]) |
| — perpetual-inventory cost side | **Dr** COGS / **Cr** Inventory (at cost) | [verified] |
| **Cash sale** | **Dr** Cash / **Cr** Sales Revenue | [verified] |
| **Customer receipt** (payment in) | **Dr** Cash/Bank / **Cr** Accounts Receivable | [standard] |
| **Vendor bill** | **Dr** Asset or Expense (+ **Dr** Input Tax) / **Cr** Accounts Payable | [verified] (tax line [standard]) |
| **Vendor payment** (payment out) | **Dr** Accounts Payable / **Cr** Cash/Bank | [verified] |
| **Goods receipt** (perpetual) | **Dr** Inventory / **Cr** GR/IR clearing (or AP) | [standard] |
| **Fixed-asset purchase** (on credit) | **Dr** Fixed Asset / **Cr** Accounts Payable | [verified] |
| **Depreciation** | **Dr** Depreciation Expense / **Cr** Accumulated Depreciation *(contra-asset)* | [verified] |
| **Fixed-asset disposal** (general) | **Dr** Cash (proceeds) + **Dr** Accumulated Depreciation / **Cr** Fixed Asset (gross cost) + **Cr** Gain *or* **Dr** Loss on disposal | [verified] |
| **Payroll** (overview) | **Dr** Wages Expense / **Cr** Wages Payable + **Cr** Withholdings/Taxes Payable; then pay: **Dr** those payables / **Cr** Cash | [standard] |

> ⚠️ **Disposal trap (research-flagged).** The 2-line `Dr Accumulated Depreciation /
> Cr Fixed Asset` you'll see quoted is **only** the special case of a *fully-
> depreciated asset scrapped for zero proceeds*. **Do not hard-code it.** Model
> disposal generally: remove the asset's **gross cost** *and* its **accumulated
> depreciation**, book any **proceeds**, and compute the **gain/loss** vs carrying
> value.

**Key modeling note (research-flagged):** depreciation **credits a contra-asset
(Accumulated Depreciation), never the asset directly** — this preserves gross cost so
the balance sheet shows cost, accumulated depreciation, and net book value
separately. Accumulated Depreciation is a distinct account with a *credit* normal
balance, linked to the asset.

## 5. Recognition — cash vs accrual, and revenue **[verified]**

**Basis** governs *when* you post:
- **Cash basis** — record revenue/expense when cash moves. *Not GAAP-compliant*;
  permissible only for non-public entities (a special-purpose framework).
- **Accrual basis** — record revenue when **earned** and expense when **incurred**,
  regardless of cash. **Required under GAAP** for SEC public companies. Default for
  any GAAP/IFRS context.
- *Source: US Congressional Research Service R43811 (+ SEC Reg S-X corroboration).*

**Revenue recognition** is the converged **IFRS 15 / US-GAAP ASC 606** — *identical*
core principle + five-step model (strongest finding in the pass; dual primary
sources — IFRS Foundation + FASB ASU 2014-09):
1. Identify the **contract** with the customer.
2. Identify the **performance obligations**.
3. Determine the **transaction price**.
4. **Allocate** the price to obligations by relative **stand-alone selling prices**.
5. **Recognize** revenue when (or as) an obligation is **satisfied** — i.e. when the
   customer obtains **control** of the good/service.

Satisfaction is **point-in-time** (typically goods → a single revenue event) or
**over-time** (typically services → a recognition *schedule* needing a progress
measure). *Template implication:* revenue is not always "post at invoice" — for
services it may be a deferred-revenue liability that releases on a schedule.

- *deenv:* a status/lifecycle + a **deferred-revenue** account for over-time
  obligations; recognition = a scheduled posting action.

## 6. AR & AP — open items, aging, reconciliation **[standard]**

- **Open item** = an unpaid invoice/bill; payments **allocate/match** against open
  items (one payment across many invoices; partial payments). A cleared item is no
  longer open.
- **Aging** = bucketing open items by overdue age (0–30 / 31–60 / 61–90 / 90+) for
  collections (AR) and cash planning (AP).
- **Reconciliation to control accounts:** the **sum of the AR subledger open items
  must equal the AR control account balance in the GL** (same for AP). This tie-out
  is a core integrity check. *(The subledger-as-control-account architecture itself
  is [verified] §3; the aging/allocation lifecycle is [standard].)*
- *deenv:* invoices/bills are objects with a `status` + `paidAmount` (computed);
  open = balance > 0; aging = a computed bucket from due date; the control-account
  tie-out is a computed reconciliation.

## 7. Bank reconciliation **[standard]**

Match **book** cash entries against the **bank statement**: tick off matched items,
surface timing differences (uncleared cheques, deposits in transit) and bank-only
items (fees, interest) that need booking. Reconciled book balance + outstanding
items = statement balance. *deenv: a reconciliation object linking book entries to
statement lines + a computed difference that must reach zero.*

## 8. Periods, closing, the trial balance **[standard]**

- **Fiscal periods** (usually months) partition the year; **period locking**
  prevents posting into a closed period (a critical control — see §12).
- **Trial balance** = every account's debit/credit balance; **total debits must
  equal total credits** (falls out of §1). The pre-statement integrity report.
- **Year-end close:** **temporary** accounts (income, expense) are closed into
  **retained earnings** (equity) so the new year starts them at zero; permanent
  (balance-sheet) accounts carry forward.
- *deenv:* `period` objects with a `locked` status guarding posts; trial balance +
  close are computed/derived actions.

## 9. The financial statements **[standard]** — all derived from the GL

- **Balance Sheet** — Assets = Liabilities + Equity at a point in time.
- **Profit & Loss / Income Statement** — Income − Expenses over a period.
- **Cash Flow Statement** — cash movement (operating/investing/financing) over a
  period.
- All three are **computed from the GL**, not separately stored. *deenv: computed
  reports over the journal lines (pillar 2).*

## 10. Tax — VAT/GST vs US sales tax **[standard]**, localized

- **VAT / GST** is collected at each stage: **Output VAT** (on sales, a liability you
  owe) minus **Input VAT** (on purchases, reclaimable) → you remit the **net** to the
  authority periodically (the **VAT return**). Captured as tax lines on
  invoices/bills posting to Output-VAT-payable / Input-VAT-receivable accounts.
- **US sales tax** differs structurally: charged only at final retail sale, no
  input-credit chain; rate depends on ship-to jurisdiction (state/county/city).
- *Rates, thresholds, and filing formats are inherently **localized and
  fast-moving** — model tax as a **configurable layer** (rate × product × partner ×
  jurisdiction → posting accounts), never hard-coded.*
- *deenv:* tax codes as a dictionary/set; tax lines as computed on documents;
  Output/Input VAT accounts; the return = a computed period summary.

## 11. GAAP vs IFRS — practical differences **[standard]**

A template author parameterizes, not hard-codes, the standard. Notable practical
differences (not exhaustive — verify before relying):
- **Inventory:** **LIFO** is permitted under US GAAP, **prohibited under IFRS**.
- **Development costs:** capitalizable under IFRS (conditions), generally expensed
  under US GAAP.
- **Asset revaluation:** IFRS permits a revaluation model; US GAAP generally
  cost-only.
- Revenue (IFRS 15 / ASC 606) is **converged** — the big exception where they agree.

## 12. Correctness-critical rules (what templates get wrong)

- **The balanced invariant [verified]** — reject any entry where Σdebits ≠ Σcredits.
  Non-negotiable; enforce at post time.
- **Immutability of posted entries [standard, high-risk]** — a **posted** journal
  entry is **never edited or deleted**; you correct it with a **reversing entry** (an
  equal-and-opposite entry) plus the corrected one. This preserves the **audit
  trail**. *(Aligns with deenv pillar 4 — never-overwrite history; a posted entry is
  an immutable document.)*
- **Period locking [standard, high-risk]** — no posting into a closed period;
  late adjustments go to an open period.
- **Multi-currency [standard, high-risk]** — store transaction currency + rate;
  **revalue** open foreign balances at period end (IAS 21 functional vs presentation
  currency); unrealized FX gain/loss posts to the GL.
- **Rounding [standard]** — define rounding policy; rounding differences may need a
  dedicated account so entries still balance to the cent.
- **Reconciliation [standard]** — subledger totals must tie to control accounts (§6);
  bank book must tie to statement (§7).

> The research pass **verified only the balanced-invariant** of these; the rest are
> standard practice it did **not** independently confirm. They are exactly the
> high-risk items — §14 flags them for a dedicated verification pass before a
> template ships.

## 13. deenv mapping (summary) & build order

| Accounting concept | deenv primitive |
|---|---|
| Journal entry + balanced lines | object holding a set of lines; computed Σdr=Σcr invariant gating post |
| Account (chart of accounts) | object in a set; type enum + normal-side; parent self-reference |
| Document → subledger → GL | document object; **posting = action deriving GL lines** (a projection) |
| Posted entry immutable + reversing | immutable document (pillar 4); correction = new reversing object |
| Open item / aging / reconciliation | computed balance + computed bucket + computed tie-out |
| Period + locking | period object with a `locked` status guarding posts |
| Financial statements, trial balance, tax return | **computed** reports over journal lines (pillar 2) |

**Template build order (smallest useful first):**
1. **Chart of accounts + manual journal entries** with the balanced invariant — the
   irreducible engine.
2. **+ AR/AP** — customer invoices/receipts, vendor bills/payments, posting to the GL.
3. **+ Trial balance + the three statements** (computed).
4. **+ Periods/locking + reversing-entry corrections** (the immutability discipline).
5. **+ Tax (VAT/sales) as a configurable layer**, fixed assets/depreciation, bank rec.
6. **+ Multi-currency + revaluation** (the hard correctness layer).

## 14. What to research next (before shipping a template that needs it)

The research pass was honest that these brief topics did **not** reach verified
claims — get authoritative citations before relying on them:
- The **remaining postings** (customer receipt, bank/cash transfers, perpetual goods
  receipt + COGS standalone, payroll detail, VAT capture + settlement entries).
- The **correctness rules** as standards/vendor-cited: immutability + reversing
  entries + audit trail, multi-currency revaluation (IAS 21), rounding, period lock.
- **AR/AP aging + allocation + control-account reconciliation** lifecycle, and **bank
  reconciliation** process.
- **Statement derivation + year-end close** mechanics, and the **GAAP-vs-IFRS**
  practical differences to parameterize.
- A **jurisdiction-specific pass** (e.g. EU/Czech VAT) when a concrete deployment
  needs it.

## 15. Sources (verified core)

**Primary:** IFRS Foundation — IFRS 15; FASB — ASU 2014-09 (ASC 606); Microsoft
Learn — Dynamics 365 Finance, ledger/subledger; US Congressional Research Service —
R43811 (cash vs accrual); Open University OpenLearn — bookkeeping.
**Secondary (corroborating):** AccountingTools (key journal entries); Wikipedia
(double-entry bookkeeping); double-entry-bookkeeping.com (normal balances); plus
SAP/Oracle architecture parallels, and engineering write-ups on immutable
double-entry ledgers (Square "Books", Formance).

*Verification: 25 claims, 3-vote adversarial, 25 confirmed / 0 killed. The
**[standard]** sections above are outside that verified set — see §14.*
