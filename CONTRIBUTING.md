# Contributing to FieldKit

Thanks for contributing. FieldKit optimizes for **small, well-tested, single-purpose pull requests
that one human can review quickly and confidently** — and for docs that never drift from the code.

## The one rulebook

**→ [docs/engineering/pull-requests.md](docs/engineering/pull-requests.md)** is authoritative for how
to open a PR. Read it before your first contribution. In short:

- **One PR, one purpose.** Small (soft budget ~≤400 hand-written diff lines); stack bigger work.
- **Behavior changes ship tests.** Bug fixes ship a regression test that fails before the fix.
- **Docs move with code.** Update the owning spec / ADR / module registry in the *same* PR.
- **Cite spec IDs** (`VIS-01`, `ORD-12`, …) and the [delivery-plan](docs/roadmap.md) week.
- **Gates are non-negotiable:** architecture tests + tenant-isolation tests must pass; the banned
  patterns (`IgnoreQueryFilters`, `DateTime.Now`, raw tenant-bypass) fail CI.
- **Draft first**, self-review your own diff, mark ready when CI is green. **A human merges.**

## For AI agents

Agents (Claude Code and others) **must** follow [docs/engineering/pull-requests.md §8 (Agent rules)](docs/engineering/pull-requests.md#8-agent-rules-imperative--an-agent-must-follow-these):
never merge/approve, never force-push `main`, never bypass hooks/signing, never commit secrets, and
**stop and ask** on auth/tenancy, public-contract, destructive-migration, or secrets/infra changes.
[CLAUDE.md](CLAUDE.md) points agents here automatically.

## Where things live

- Product & functional specs → [`docs/product/`](docs/README.md)
- Architecture & decision records → [`docs/architecture/`](docs/architecture/00-architecture-overview.md)
- Wireframes → [`docs/ux/`](docs/ux/README.md)
- What gets built when → [roadmap](docs/roadmap.md) · [delivery plan](docs/delivery-plan.md)
