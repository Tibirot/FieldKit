# Functional Spec — Outlets (Master Data)

> **Module:** Outlets · **Group:** Admin · **Phase:** 1 · **Status:** ✅ Baseline
> **Depends on:** Organization · **Consumed by:** Journey, Visit, Audit, Order

## 1. Purpose

Outlets is the **trade universe** — the retail points of sale a rep visits. It is the master
data that anchors journeys (where to go), visits (where you are), audits (whose shelf), and
orders (who's buying). Getting this clean and well-classified is what makes everything
downstream possible.

## 2. Actors

| Actor | Interest |
|---|---|
| Sales Ops / Admin | Maintain accurate outlets, classification, and geo |
| Field Rep | Sees their outlets; can propose corrections from the field |
| Supervisor | Reviews coverage of the outlet base |

## 3. Core concepts

- **Outlet (POS)** — a retail location: name, code, address, **geo-coordinates**, **IANA
  timezone**, status.
- **Timezone** — an explicit IANA zone on the outlet (e.g. `Europe/Bucharest`). Required because
  promotion validity ([BR-PRD-6](13-products-and-pricing.md#5-business-rules)) and a visit's
  business "day" resolve **in the outlet's timezone**, and a rep may cross zones. Seeded from geo on
  import, editable; not derived on-device.
- **Channel** — trade classification (e.g. Modern Trade, Traditional Trade, HoReCa). Drives
  assortment, pricing, visit workflow, and audit forms.
- **Segment / tier** — a finer grade (e.g. A/B/C by volume) influencing call frequency.
- **Banner / chain** — the retail group an outlet belongs to (optional).
- **Order-block / credit standing** — a flag that **blocks order submission** (e.g. a debtor on
  credit hold). Checked at order submit and on the sync push path (as-of-now); a blocked outlet
  rejects the order with reason `OUTLET_ON_HOLD`.
- **Contacts** — people at the outlet (store manager, buyer); **personal data**
  ([B8](decisions-and-assumptions.md#b8--privacy--gdpr-posture)).
- **Custom fields** — per-tenant attributes ([A1 config-driven](decisions-and-assumptions.md#a1--per-tenant-customization-config-driven-moderate)).
- **Geofence** — the outlet's location + radius, used by Visit check-in.

## 4. Capabilities & flows

### F1 · Maintain outlets
- CRUD outlets with classification (channel, segment, banner), address, geo, contacts, and
  tenant custom fields. Bulk import for onboarding.

#### Location, time zone and contacts

**The address is structured**, not one free-text block: postal code and city are what territory
membership rules key off (`ORG-07`), and a single string would make those rules parse prose. Every
part is optional — onboarding data is routinely half-known, and an address that must be complete
before it can be recorded means a half-known outlet cannot be recorded at all.

**The time zone is required and explicit** — `Europe/Bucharest`, never an offset, and never derived
from the coordinates. A visit's business "day" and a promotion's validity ([BR-PRD-6](13-products-and-pricing.md#5-business-rules))
both resolve in it, a rep may cross zones during a shift, and an offset is wrong twice a year.
Deriving it on the device would make the answer depend on which device asked. It is validated against
the runtime's zone database rather than a pattern, because `Europe/Bucuresti` is well-formed and does
not exist.

**Coordinates are optional**, and whether supplied ones are validated is a per-tenant setting
(`validateGeoCoordinates`, default on). When it is on, a supplied point must be on the earth; when it
is off, no coordinate validation happens. An outlet with **no** coordinates is never rejected either
way — the setting does not make them required, and BR-OUT-2's "required for outlets that participate
in journeys" lands with the Journey module, where participation is defined.

> 📝 ASSUMPTION: the consequence of the flag being tenant-controlled. While validation is off,
> out-of-range coordinates can be stored. Turning it on afterwards does not clean them, and the next
> save of such an outlet fails against data already in the table. A tenant enabling this on an
> existing estate should expect to fix rows, not just flip a switch.

**Contacts are personal data** ([B8](decisions-and-assumptions.md#b8--privacy--gdpr-posture)). They
are replaced wholesale on update rather than patched — a delta needs the caller to know the current
state, and two people editing one outlet would interleave silently. It also gives erasure a trivial
shape: an empty list removes every contact, and the rows are deleted rather than flagged. A dedicated
erasure workflow is `OUT-10`.

### F2 · Classify & assign
- Assign an outlet to a **channel** (mandatory — it drives assortment/pricing/workflow) and to
  a **territory** (via Organization).

### F3 · Field-originated changes
- A rep can **propose** an outlet correction (moved location, new contact, wrong data) from the
  field; it enters a review queue rather than editing master data directly.
- A rep can **request a new outlet** (prospecting) → review → becomes real master data.

### F4 · Lifecycle
- Outlets can be `Active`, `Inactive` (temporarily not visited), or `Closed` (permanent).

`Closed` is **terminal**: an outlet cannot be reopened. That is what makes it mean anything beyond
`Inactive` — a status that can be walked back is just a long-lived `Inactive`, and BR-OUT-4's
"excluded from new journeys, retains history" would be a preference rather than a fact. A location
that genuinely reopens is a **new outlet with its own code**, because its trading history as a
different business should not silently continue.

> 📝 ASSUMPTION: no back-office "reopen" escape hatch. An outlet closed by mistake has to be
> re-created under a new code, which loses the link to its history. If operators hit this in
> practice, the answer is an explicit, separately-permissioned reopen that records who did it —
> **not** relaxing the transition, which would take the meaning out of `Closed`.

Status changes go through their own endpoint rather than the edit form. "This store is shut" is a
different decision from "the name was spelled wrong", and merging them lets a careless update close
an outlet as a side effect of fixing a typo.

**Every transition is recorded, append-only.** Neither `Inactive` nor `Closed` deletes anything — but
the outlet's own audit stamps are overwritten by the next ordinary edit, so without a trail an outlet
closed in March and renamed in April reads as though nobody ever closed it. The trail holds
`from → to`, the reason, when, and who; it starts with the outlet's creation (`from` is null), so
"no history" can never be mistaken for "the history was lost". There is no API to write, edit or
delete an entry — an audit log with a write path is one that can be arranged after the fact.

**A reason is required to close, and optional otherwise.** Closing is irreversible and removes the
outlet from every future journey, so *why* is the question an auditor will ask about it, and the
person who knows the answer is the one doing it. Demanding a reason for a routine
`Active`↔`Inactive` toggle would buy a column full of ".".

## 5. Business rules

- **BR-OUT-1** Every outlet has a **channel** and a **primary territory**.
- **BR-OUT-2** Geo-coordinates are required for outlets that participate in journeys/geofenced
  check-in (validated on save).
- **BR-OUT-3** Field-originated changes are **proposals**; master data is only mutated by an
  authorized back-office approval (keeps the reference data server-authoritative — [B7](decisions-and-assumptions.md#b7--conflict-resolution-matrix)).
- **BR-OUT-4** A `Closed` outlet is excluded from new journeys but retains history.
- **BR-OUT-5** Custom fields validate against the tenant's field definitions.

## 6. Requirements

| ID | Requirement | MoSCoW | Phase |
|---|---|---|---|
| OUT-01 | CRUD outlets with channel, segment, geo, address, contacts | Must | 1 |
| OUT-02 | Per-tenant custom fields on outlets | Must | 1 |
| OUT-03 | Assign outlet to channel + territory | Must | 1 |
| OUT-04 | Outlet lifecycle (Active/Inactive/Closed) | Must | 1 |
| OUT-05 | Bulk import of outlets (onboarding / demo seed) | Should | 1 |
| OUT-06 | Rep-proposed outlet corrections → review queue | Should | 3 |
| OUT-07 | Rep-requested new outlet (prospecting) → review | Should | 3 |
| OUT-08 | Geofence config (radius) per outlet/channel | Should | 2 |
| OUT-09 | Map view of the outlet base | Could | 4 |
| OUT-10 | Contact PII handling & erasure hooks | Could | 4 |

## 7. Offline behavior

Outlets are **reference data**: pulled to the device (territory-scoped, [A4](decisions-and-assumptions.md#a4--offline-data-scope-territory-scoped))
and **read-only** on device. Rep corrections/new-outlet requests are captured offline as
**proposals** and pushed via the outbox; they never mutate master data directly (they enter the
review queue on the server). This keeps outlets conflict-free ([B7](decisions-and-assumptions.md#b7--conflict-resolution-matrix)).

## 8. Module contract (exposed to others)

- `IOutletCatalog` — resolve outlet by id; list by territory/channel; geofence, timezone,
  order-block flag.
- `IOutletClassification` — channel/segment of an outlet (used by Products, Journey, Audit).
- `IReferenceChangeFeed` (sync source) — territory-scoped, row-version delta of outlets with
  tombstones, for **Sync** ([module boundaries §7](../architecture/10-module-boundaries.md#7-module-registry)).
- `IOutletProposalIngest` — apply a pushed outlet **proposal** (correction / new-outlet request)
  into the review queue through this module, used by **Sync** (proposals never mutate master data
  directly, [§7](#7-offline-behavior)).
- Consumes `ITerritoryDirectory` (Organization) and `IFieldDefinitionCatalog` (Configuration —
  custom-field validation, BR-OUT-5).
- Publishes `OutletChanged`, `OutletClosed` → Journey/Sync react.

## 9. Acceptance criteria (sample)

- Saving an outlet without a channel is rejected.
- A rep's offline correction appears in the back-office review queue after sync and does not
  alter the outlet until approved.

## 10. Open questions

- Do banners/chains need modeling in v1, or defer? (Assumed: optional field, no chain-level
  logic in v1.)
- Approval SLA/roles for the review queue. (Assumed: any Sales Ops user.)
