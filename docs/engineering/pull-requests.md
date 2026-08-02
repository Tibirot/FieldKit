# Pull Request Guidelines (for humans and agents)

> **Status:** ✅ Baseline · **Audience:** every contributor, **especially AI agents** · **Authority:** this is the rulebook a PR is judged against.

FieldKit optimizes for one thing here: **a single human can review a PR correctly, quickly, and
without dread.** Everything below serves that. Agents must treat these as hard rules; humans as
strong defaults. When a rule and reviewer judgement conflict, the reviewer wins — but the agent
must not *pre-empt* that by ignoring a rule.

> **Prerequisite:** these presuppose the repo is a git repository with a GitHub remote and CI
> (roadmap [Phase 0](../roadmap.md#phase-0--foundation-in-progress)). Until then this is a written contract, not
> a runnable workflow.

---

## 1. Principles

1. **One PR, one purpose.** A PR does exactly one reviewable thing. No feature + refactor in the
   same PR; no drive-by cleanups riding along.
2. **Small enough to hold in your head.** If a reviewer can't reconstruct the change from the diff
   in one sitting, it's too big — split it.
3. **If it changes behavior, it ships tests.** No exceptions for "trivial" logic.
4. **Code and docs move together.** A behavior change updates its spec; an architectural change adds
   an ADR — *in the same PR*. FieldKit's docs are never allowed to drift from the code.
5. **Green before review.** A PR is only marked *ready* when CI passes. Reviewers review ready PRs.
6. **The human merges.** Agents author, self-review, and iterate; a person approves and merges.

---

## 2. Scope & size — the reviewability budget

- **One module or one concern per PR** where possible. FieldKit's [module boundaries](../architecture/10-module-boundaries.md)
  make this natural: a PR that sprawls across many modules usually signals a missing seam or a PR
  that should be split.
- **Soft budget: ≤ ~400 lines of hand-written diff.** Past that, prefer **stacked PRs**. This is a
  guideline, not a gate — a cohesive change slightly over is fine; a 1,200-line PR is not.
- **Excluded from the budget** (but call them out in the description): generated migrations, lockfiles,
  generated OpenAPI/clients, snapshot/vector fixtures, moved-not-changed files. Put large mechanical
  changes in their **own** PR so they don't bury logic.
- **A delivery-plan week is many PRs, not one.** Decompose a [week](../delivery-plan.md) into the
  smallest independently-mergeable, independently-demoable slices. Stacking order: contracts/schema →
  domain → application → API → UI.

**How to split when it's too big**
- By layer (contract/migration first, then implementation).
- By module (each module's slice separately).
- By "make it work" then "make it nice" (behavior PR, then a separate refactor PR).
- Behind a **feature flag** so an incomplete feature can merge in slices without shipping to users.

---

## 3. Tests — what "covered" means here

Map every change to FieldKit's [testing strategy](../architecture/17-testing-strategy.md):

- **New behavior → tests for it.** New public/domain behavior gets unit tests; a use-case that
  crosses a module gets an integration test on **real Postgres (Testcontainers)**.
- **Bug fix → a regression test that fails before the fix and passes after.** Include it; don't just
  describe it.
- **Touching the pricing or perfect-store engines** → the **generated C#≡TS parity vectors** must
  pass ([BR-PRD-8/9](../product/13-products-and-pricing.md#decimal-parity-resolves-finding-s4),
  [BR-AUD-5/12](../product/22-merchandising-and-audits.md#5-business-rules)).
- **Touching the sync engine / offline paths** → the **property-based sync tests** (chaos
  connectivity, idempotency replay, scope-entry, drain, kill-during-capture) must cover the new
  path ([sync tests](../architecture/17-testing-strategy.md#5-sync-engine-tests-the-hard-part--property-based)).
- **Multi-tenant data access** → a **tenant-isolation test** (two tenants; a crafted cross-tenant id
  yields not-found, never data).
- **Coverage must not drop** for changed files. Coverage is a floor, not a target — don't write
  assertion-free tests to hit a number.

**Non-negotiable gates (fail = cannot merge):**
- **Architecture tests** (AT-1…AT-8): no module→module internals, contracts-only public surface, no
  entity leakage, `DbContext`-maps-own-schema, **no `IgnoreQueryFilters`/raw tenant-bypass**,
  `IClock`-only time ([boundaries §5](../architecture/10-module-boundaries.md#5-enforcement--architecture-tests)).
- **Tenant-isolation tests.**
- These two are **required status checks** — branch protection blocks merge without them.

---

## 4. Traceability & docs-in-lockstep

Every PR answers *"what does this implement and where is that written down?"*

- **Cite the spec requirement IDs** the PR implements or changes: e.g. `Implements VIS-01, VIS-02`,
  `Fixes ORD-12`. The reviewer checks the diff against those IDs to confirm scope.
- **Reference the delivery-plan week / roadmap phase** the work belongs to.
- **Docs in the same PR:**
  - New/changed behavior → update the owning **functional spec** (and the [module registry](../architecture/10-module-boundaries.md#7-module-registry) if a contract/event changed).
  - New/changed architectural decision → add or supersede an **[ADR](../architecture/adr/README.md)**
    (never edit an accepted ADR to reverse it — add a new one).
  - New capability/module → update the [capability map](../product/00-product-overview.md#4-capability-map) and counts.
- **If a PR can't cite a spec ID,** either the work is unspecified (write the spec first, possibly in
  a prior PR) or it's out of scope.

---

## 5. Branch, commit, and PR mechanics

- **Never commit to `main`.** Branch from an up-to-date `main`.
- **Branch name:** `type/short-slug` — `feat/visit-checkin`, `fix/order-reprice-flag`,
  `docs/adr-0012-…`, `chore/ci-arch-tests`, `refactor/pricing-engine`.
- **Conventional Commits** for messages and PR title: `feat(visit): geofenced check-in (VIS-01)`.
  Types: `feat` `fix` `docs` `refactor` `test` `chore` `perf` `build` `ci`. Scope = module.
- **Small, logical commits.** Don't squash locally into one opaque blob; let the reviewer read the
  steps. (Final squash-on-merge is the human's choice.)
- **Rebase on `main`, don't merge `main` in,** to keep history linear (unless the team decides
  otherwise).
- **Draft first.** Open as a **draft PR**, self-review, then mark **ready** once CI is green.

---

## 6. The PR description

Use the [PR template](../../.github/pull_request_template.md). It must contain:

- **What & why** — the change and its motivation, in plain language.
- **Scope** — spec IDs, module(s), delivery-plan week; what's explicitly *not* in this PR.
- **How it was tested** — which tests, and evidence the gates pass.
- **Docs updated** — which specs/ADRs/registry entries changed.
- **UI** — **before/after screenshots or a short GIF** for any user-facing change (compare against the
  [wireframes](../ux/README.md)).
- **Risk & rollback** — blast radius; how to revert; migration reversibility; feature-flag state.
- **Pre-PR agent review** (agent-authored PRs) — which model reviewed, its verbatim findings, and
  your disposition for each (§8).
- **Reviewer notes** — anything non-obvious, and where to start reading.

**Self-review:** before requesting review, the author (agent included) reads its own diff and leaves
**inline comments** on anything non-obvious — the reviewer should never have to ask "why is this here?"

---

## 7. Migrations & risky changes

- **DB migrations** follow **expand → migrate → contract** (never a breaking rename in one step) so a
  rollback is safe; each module owns its migration ([data & persistence](../architecture/14-data-and-persistence.md#6-migrations)).
- A migration PR is ideally **separate** from the code that uses the new shape.
- **Risky or incomplete features** land behind a **feature flag**, defaulted off.
- **Backwards-compatible API changes** are additive; a breaking change bumps the version
  ([API contracts §5](../architecture/13-api-contracts.md#5-versioning)) and is called out loudly.

---

## 8. Agent rules (imperative — an agent MUST follow these)

**Do**
- Keep the PR within scope and the size budget; **split** rather than exceed.
- Write/adjust tests for every behavior change; make a bug's regression test fail first.
- Update the owning spec/ADR/registry in the same PR.
- Run the full local gate before opening (`build → lint → unit → arch-tests → tenant-isolation →
  integration`); only open the PR if they pass.
- **Get the diff reviewed by an independent frontier-model agent before opening the PR** (below).
- Claim no more in the PR body than the diff and that review support.
- Open as **draft**, fill the template, cite spec IDs, self-annotate the diff, then mark ready.
- Use `gh pr create` (see §9).

**Pre-PR review — an independent agent, before `gh pr create` (required)**

Once the gate is green and the work is committed, hand the diff to a **fresh agent running a
frontier model** — a current top-tier model, not a small/fast one — and have it review the change
*before the PR exists*. Self-review catches slips but not a wrong premise, because the same agent
chose the premise. In Claude Code: the `Agent` tool with `model: "fable"`; pick a different frontier
model if the authoring agent is already running that one.

- **Give it the diff, this rulebook, and the specs the change cites — not your rationale.** A
  reviewer handed a justification grades the justification instead of the change.
- **Brief it adversarially:** defects, unstated assumptions, missing tests, scope creep, wrong facts.
  "Looks good" is not an outcome — if it reports nothing, say so in the PR and note that a clean
  first round is weak evidence, so the human should weight their own review accordingly.
- **Review the right diff:** `git diff <pr-base-branch>...HEAD`. On a stacked PR the base is the
  parent branch, not `main` — otherwise the review re-covers work already reviewed and merged.
- **Paste the reviewer's verbatim findings into the PR** in a collapsed `<details>` block, with your
  disposition against each. A paraphrase is unverifiable; the human must be able to see what was
  raised, especially what you chose not to act on.
- **Every finding is fixed or answered.** Silently dropping an inconvenient one is what would make
  this step theatre.
- **Fixes that materially change the diff get a follow-up pass** over the delta; the PR records the
  final round.
- **Pushes to an open PR** that change behavior, tests, or contracts get the same treatment.
  Typo-level fixups don't.
- **Never an approval.** It does not substitute for human review and is never a reason to merge.

This applies to **every** agent-authored PR, including small and docs-only ones. That is deliberate
rather than an oversight: an agent judging its own change "too trivial to review" is exactly the
judgement this step exists to check, and the cost is one agent call. Scale the review's depth to the
change, not its existence.

**Never**
- **Skip the pre-PR review** because the change looks small, obvious, or docs-only.
- **Present a review as clean when findings were dismissed** — list them and the reasoning.
- **Merge, approve, or dismiss reviews** — that's the human's gate.
- **Force-push `main` or any shared branch;** commit to `main`; delete branches you don't own.
- **Bypass hooks or signing** (`--no-verify`, `--no-gpg-sign`) — if a hook fails, fix the cause.
- **Commit secrets** (keys, tokens, connection strings, `.env`, user-secrets) or unrelated files.
- **Weaken a gate to go green** (e.g. add `IgnoreQueryFilters`, delete a failing arch-test,
  loosen a tenant check). If a gate is genuinely wrong, that's a separate PR with justification.

**Stop and ask the human first** when the change involves:
- authentication, authorization, or **tenant isolation**;
- a **public module contract or integration event** signature (ripples across modules);
- a **destructive migration** or data backfill;
- **secrets / infra / deployment** config;
- anything that would exceed the size budget and can't be cleanly split.

---

## 9. Workflow (once the repo + `gh` exist)

```bash
# 1. fresh branch off main
git switch main && git pull --rebase
git switch -c feat/visit-checkin

# 2. implement + tests + docs, in small commits

# 3. run the local gate (must be green)
dotnet build && dotnet test          # unit + arch-tests + integration (Testcontainers)
# + frontend (from frontend/): npm run lint && npm test && npm run build
#   dependency change? regenerate the lockfile — docs/engineering/frontend-toolchain.md

# 4. independent review by a frontier-model agent, BEFORE the PR exists (§8)
#    Claude Code: the Agent tool with model "fable", over `git diff <base>...HEAD`
#    (<base> is the PR's target branch — the parent branch when stacking, not always main).
#    Give it the diff + this rulebook + the cited specs — not your rationale.
#    Fix or answer every finding; verbatim findings + dispositions go in the PR body.

# 5. open a DRAFT PR, template filled, spec IDs cited
gh pr create --draft \
  --title "feat(visit): geofenced check-in (VIS-01, VIS-02)" \
  --body-file .github/pull_request_template.md   # then edit in the specifics

# 6. self-review the diff, leave inline notes, wait for CI green
# 7. mark ready
gh pr ready
```

---

## 10. Definition of Done (the merge checklist)

- [ ] One purpose; within the size budget (or cleanly stacked).
- [ ] Behavior changes covered by tests; bug fixes have a failing-first regression test.
- [ ] Architecture-test + tenant-isolation gates green; no banned patterns.
- [ ] Sync/pricing/score changes: property + parity suites green.
- [ ] Owning spec / ADR / module registry updated in this PR (docs-in-lockstep).
- [ ] Spec IDs and delivery-plan week cited; scope (and non-scope) stated.
- [ ] UI changes have before/after screenshots.
- [ ] Migration is reversible / expand-contract; risky work behind a flag.
- [ ] Reviewed by an independent frontier-model agent before the PR was opened, with its verbatim
      findings and your dispositions in the PR (if agent-authored).
- [ ] Self-reviewed with inline notes; CI green; opened as draft → marked ready.
- [ ] No secrets, no unrelated files, no gate weakened to pass.
