# Functional Spec — Organization & Territory

> **Module:** Organization · **Group:** Admin · **Phase:** 1 · **Status:** ✅ Baseline
> **Depends on:** IAM · **Consumed by:** Outlets, Journey, Visit

## 1. Purpose

Organization models **who sells where**. It captures the sales hierarchy (the people and their
management lines) and the **territories** that carve the market up, then **assigns reps to
territories**. Journey planning and outlet ownership both hang off this: an outlet belongs to a
territory, a territory is served by a rep, a rep reports up a management chain.

## 2. Actors

| Actor | Interest |
|---|---|
| Sales Ops / Admin | Build the org tree, define territories, assign reps |
| Supervisor | Owns a branch of the tree; sees their team's territories |
| Field Rep | Is assigned to one (or more) territories that scope their world |

## 3. Core concepts

- **Org unit** — a node in the sales hierarchy (e.g. Country → Region → Area → Team). Tenants
  configure the depth/labels.
- **Position / assignment** — a user occupying a role in the tree (e.g. Andrei = Supervisor of
  *Area North*), establishing the management line.
- **Territory** — a bounded slice of the market a rep is responsible for. Defined by the set of
  **outlets** it contains (membership-based; optionally aided by geo/postal rules).
- **Rep–territory assignment** — which rep serves which territory, over a validity period.

## 4. Capabilities & flows

### F1 · Model the org hierarchy
1. Admin builds the tree of org units and assigns users (supervisors/reps) to positions.
2. The management line is derived (used for roll-up reporting and visibility scoping).

Two shapes of that derivation, from the same tree and answering different questions:

- **Management line** — the units *above* someone, nearest first. Who they report up through.
- **Visibility scope** — their units and everything *below*. What BR-ORG-4 describes.

A **position's title is a label, never a capability.** It is free text an admin types: it can read
"Supervisor" for someone holding no supervisory permission, and anyone with `position:write` can
change it. What a user may do comes from their token and nowhere else (BR-IAM-2) — the moment
something branches on the title, the permission model has a second, editable, unenforced copy.

Positions are **current state, not history**. A row means "is attached now". `ORG-08` (reassignment
with history) is Phase 2, and BR-ORG-5 does not depend on this either way: a visit or an order
records the user who made it, so its attribution survives any change to the org chart.

### F2 · Define territories
1. Admin creates a territory and assigns outlets to it (directly, or by geo/postal/segment
   rule that materializes membership).
2. An outlet belongs to **exactly one** primary territory at a time.

### F3 · Assign reps
1. Admin assigns a rep to a territory (with an effective date range).
2. This assignment is the input to **journey generation** and defines the rep's **offline data
   scope** ([A4 territory-scoped](decisions-and-assumptions.md#a4--offline-data-scope-territory-scoped)).

### F4 · Reassignment / coverage
- Reassign a territory to a different rep (e.g. holiday cover) with an effective date; history
  is retained.

## 5. Business rules

- **BR-ORG-1** An outlet has exactly one **primary** territory at any moment (secondary/overlay
  territories are a *Could*, Phase 4).
- **BR-ORG-2** A territory has at most one **active** rep assignment at a time; overlapping
  assignments are rejected.
- **BR-ORG-3** A rep's offline scope = the union of outlets in their active territory
  assignment(s) ([A4](decisions-and-assumptions.md#a4--offline-data-scope-territory-scoped)).
- **BR-ORG-4** Visibility: a supervisor sees data for territories under their org branch;
  enforced via permissions + the management line.
- **BR-ORG-5** Reassignment never deletes historical visits/orders — those stay attributed to
  the rep who made them.

## 6. Requirements

| ID | Requirement | MoSCoW | Phase |
|---|---|---|---|
| ORG-01 | CRUD org units; build a configurable-depth hierarchy | Must | 1 |
| ORG-02 | Assign users to positions; derive the management line | Must | 1 |
| ORG-03 | CRUD territories; assign outlets to a territory | Must | 1 |
| ORG-04 | Assign a rep to a territory with an effective date range | Must | 1 |
| ORG-05 | Enforce single-primary-territory per outlet | Must | 1 |
| ORG-06 | Rep offline scope resolvable from active assignments | Must | 2 |
| ORG-07 | Territory membership by geo/postal/segment rule | Should | 2 |
| ORG-08 | Reassignment/cover with history retained | Should | 2 |
| ORG-09 | Supervisor visibility scoping via management line | Should | 2 |
| ORG-10 | Secondary/overlay territories | Could | 4 |

## 7. Offline behavior

Read-only on device: the rep's app knows its territory + assignment (to scope sync) but org
administration is a back-office, online activity. A reassignment takes effect on the rep's next
sync (they receive a new outlet/journey scope).

## 8. Module contract (exposed to others)

- `ITerritoryDirectory` — territory of an outlet; outlets of a territory; active rep of a
  territory.
- `IRepScope` — resolve a rep's current offline scope (outlet ids) for Sync.
- `IOrgHierarchy` — management line / visibility scope for a user (used by reporting & auth).
- Publishes `RepAssignmentChanged` (integration event) → Sync/Journey react.

## 9. Acceptance criteria (sample)

- Assigning a second active rep to a territory is rejected with a clear error.
- Moving an outlet to a new territory changes the offline scope of both affected reps on their
  next sync.

## 10. Open questions

- Do reps ever legitimately serve overlapping territories concurrently? (Assumed: no in v1.)
- Is territory membership primarily manual or rule-driven for the demo data? (Assumed: manual +
  optional rule.)
