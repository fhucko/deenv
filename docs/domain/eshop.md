# Eshop — domain reference

An eshop is the **order-to-cash slice of an ERP** (see [erp.md](erp.md) §4) wrapped
in a **customer-facing storefront**: a product catalog, a cart→checkout funnel,
online payment, and fulfillment. Most of the back-office (orders, inventory,
invoicing, accounting posting) is *exactly* the ERP core — this doc covers the
**eshop-specific additions** and points back to erp.md for the shared parts.

> **Scope honesty.** The **data model + back-office** here is Stage 1. The **public,
> concurrent, logged-in storefront** (many shoppers live at once, live stock,
> per-customer sessions) leans on the **real-time / multi-user pillar (Stage 3)** —
> not a Stage-1 capability. And **payment-gateway** integration crosses the
> irreducible C# boundary (network + crypto — DECISIONS "C# is the kernel"). Build
> the model now; the concurrent storefront and live payment come with their pillars.

---

## 1. What it shares with the ERP (don't rebuild — see erp.md)
- **Product / Item** + **Inventory** (on-hand, stock moves, availability) — erp.md §3, §6.
- **Sales Order → Delivery → Invoice → Payment** (order-to-cash) — erp.md §4.
- **Accounting posting** (revenue, tax, COGS, AR) — erp.md §7.
- **Partner** = the customer master record — erp.md §3.

So ~80% of an eshop is the ERP O2C + inventory model. The genuinely new parts are
the **catalog**, the **cart→checkout funnel**, the **storefront/back-office split**,
and **gateway payment**.

## 2. Eshop-specific entities

**Catalog / Product (web-facing)** — the storefront view of an item:
- Fields: name, slug/URL, rich description, images, list price, sale price,
  category(ies), **variants** (size/colour → each a sellable item with own
  SKU/stock/price), availability, SEO/meta, published/visibility flag.
- *deenv:* products set; variants a sub-set; categories references or a dictionary.

**Category / Collection** — navigation tree (parent self-reference) + curated
collections. *deenv: self-referencing set, or a dictionary.*

**Cart / Basket** — a transient pre-order owned by a session/customer:
- Lines: product-variant (ref), quantity, unit price (snapshot), line total.
- Computed: subtotal, discounts, shipping, tax, **grand total**.
- Lifecycle: `active → checked-out (becomes an Order) → abandoned`.
- *deenv:* the **association object** again — a set of cart lines; the cart→Order
  conversion at checkout is the key eshop action.

**Customer account** — registration, login, saved addresses, order history,
wishlist. *Login/auth is app-level behaviour + the irreducible session boundary
(DECISIONS "C# is the kernel"); the customer-as-logged-in-user is multi-user (Stage
3).* *deenv: partner + addresses + a link to their orders.*

**Checkout** — a guarded *flow*, not really an entity:
`cart → identify/login → shipping address → shipping method → payment → place order →
confirmation`. Each step validates; the output is an **Order** + a **Payment**.

**Payment** — captured via a **gateway** (Stripe/PayPal/…):
- Fields: order (ref), method, amount, gateway reference, status (pending →
  authorized → captured → refunded → failed).
- *External integration:* the gateway call is the kernel/OS network boundary + app
  logic; the *result* posts the ERP customer payment (DR bank / CR AR).

**Shipping method / rate** — carrier options, cost rules (flat / weight / zone),
**tracking**. Fulfillment itself = the ERP **delivery** (stock out).

**Discount / Coupon / Promotion** — promo codes; cart- or line-level; conditions
(min spend, category, first-order). *deenv: a discount-rules set, applied as computed
adjustments on the cart.*

**(Optional)** reviews/ratings, wishlist, recently-viewed, related products.

## 3. The storefront lifecycle (an order, web-flavoured)
`browse catalog → add to cart → checkout → Order placed (payment pending) → paid →
fulfilling (pick/pack) → shipped (tracking) → delivered → completed`; plus
`cancelled`, `returned/refunded` (reverse delivery + payment — erp.md returns).

Two faces of one model: the **storefront** (public, read-mostly catalog + the
customer's own cart/orders) and the **back-office** (admin: products, orders,
inventory, fulfillment — the ERP O2C UI).

## 4. Computed values
Cart/line totals, applied discounts, shipping cost, tax (by ship-to jurisdiction),
grand total; per-variant **availability** (on-hand − reserved); order / payment /
fulfillment status rollups.

## 5. deenv mapping & the stage line
- Catalog/products/variants → objects + sub-sets; categories → tree (self-ref) or dictionary.
- Cart & order lines → the **association object** (same shape as ERP).
- Cart→Order, place-order, capture-payment → **actions**.
- Storefront vs back-office → **two UIs over one model** (custom `fn render()` for the
  storefront; the generic UI for the back-office).
- **Stage line:** model + back-office = **Stage 1**; concurrent public logged-in
  storefront + live stock + sessions = **Stage 3** (real-time/multi-user); live
  **payment gateway** = external integration. Build the model now.

## 6. Features
Catalog + categories + variants + images + search/filter; cart; guest + account
checkout; addresses; shipping methods + rates + tracking; tax at checkout; promo
codes/discounts; payment gateway (authorize/capture/refund); order management +
fulfillment; customer order history; inventory availability + reservations;
returns/refunds; (optional) reviews, wishlist, abandoned-cart recovery. Back-office =
the ERP O2C + inventory feature set (erp.md §4, §6).

## 7. Template build order
1. **Catalog + back-office orders** — products/variants/categories + a manual Order
   (the ERP O2C model). The shippable core; no public storefront yet.
2. **+ Cart→checkout** — the customer-facing funnel producing an order.
3. **+ Fulfillment & payment status** — delivery (stock out) + payment states.
4. **+ Discounts, shipping rates, tax at checkout.**
5. **Public concurrent storefront** — *defer to the real-time/multi-user pillar*
   (logged-in shoppers, live stock); **payment gateway** as an external-integration
   slice.
