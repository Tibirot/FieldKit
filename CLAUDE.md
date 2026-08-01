# CLAUDE.md — agent instructions for FieldKit

FieldKit is a **Sales Force Automation (SFA) platform** for FMCG field sales — a **modular monolith**
on **.NET Aspire** with an offline-first **Next.js** PWA. It is a portfolio project; **documentation
and engineering discipline are deliverables**, not overhead.

## Read first

- **Docs index:** [docs/README.md](docs/README.md) — the map.
- **Decisions ledger:** [docs/product/decisions-and-assumptions.md](docs/product/decisions-and-assumptions.md) —
  the locked choices; don't re-litigate them, and honor the `📝 ASSUMPTION` markers.
- **Architecture:** [docs/architecture/00-architecture-overview.md](docs/architecture/00-architecture-overview.md)
  and the [ADRs](docs/architecture/adr/README.md) — the "why".

## Opening a pull request — MANDATORY

When you create or update a PR, follow **[docs/engineering/pull-requests.md](docs/engineering/pull-requests.md)**
to the letter. Non-negotiables:

- **One PR, one purpose; small** (soft budget ~≤400 hand-written diff lines) — **stack** bigger work.
- **Behavior changes ship tests;** bug fixes ship a **failing-first regression test**. Sync/pricing/
  score changes must pass the property + cross-language parity suites.
- **Docs move with code** — update the owning spec / ADR / [module registry](docs/architecture/10-module-boundaries.md#7-module-registry) in the same PR.
- **Cite spec IDs** (`VIS-01`, `ORD-12`, …) and the delivery-plan week.
- Keep the **architecture-test** and **tenant-isolation** gates green; never add `IgnoreQueryFilters`,
  `DateTime.Now`, or a raw tenant-bypass.
- **Draft first**, self-review your diff with inline notes, mark ready only when CI is green.

**Never** merge/approve PRs, force-push `main`, bypass hooks/signing, or commit secrets. **Stop and
ask** before touching authentication/authorization/**tenant isolation**, a **public module contract
or integration event**, a **destructive migration**, or **secrets/infra/deployment**.

## Boundaries

Respect [module boundaries](docs/architecture/10-module-boundaries.md): a module may reference only
another module's `Contracts` (never its internals); communicate via public contracts (sync) or
integration events through the outbox (async). Architecture tests enforce this — keep them green.

## Build / test (once Phase 0 lands)

The system is orchestrated by Aspire: `dotnet run --project FieldKit.AppHost`. Tests:
`dotnet test` (unit + architecture + Testcontainers integration); frontend `pnpm lint && pnpm test`.
Until [Phase 0](docs/roadmap.md#phase-0--foundation-in-progress) the repo is a scaffold — see the
[delivery plan](docs/delivery-plan.md) for what exists.
