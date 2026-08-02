---
description: Open a small, tested, single-purpose PR following FieldKit's PR rules
---

You are opening a pull request for FieldKit. The **authoritative rulebook is
`docs/engineering/pull-requests.md`** — follow it exactly. Extra context from the user: $ARGUMENTS

Work through these steps and do not skip the gates. If any gate fails or a STOP condition is hit,
halt and report — do **not** open (or force) the PR.

1. **Confirm scope & size.**
   - Determine what changed (`git status`, `git diff --stat` vs the PR's base branch — the parent
     branch when stacking, not always `main`). Confirm the change is
     **one purpose**. If it mixes a feature + refactor, or spans many modules, **propose a split
     into stacked PRs** and stop.
   - Check the size against the **~≤400 hand-written diff line** budget (exclude generated
     migrations/lockfiles/OpenAPI/fixtures, but list them). If over and not cleanly splittable,
     stop and ask the human.

2. **Confirm traceability.** Identify the **spec requirement IDs** this implements/fixes
   (`VIS-01`, `ORD-12`, …) and the **delivery-plan week / roadmap phase**. If the change can't cite a
   spec ID, the work is unspecified — flag it (the spec may need writing first).

3. **Confirm docs-in-lockstep.** Verify the owning **functional spec / ADR / module registry** were
   updated in this same change. If a behavior or contract changed and the docs didn't, update them
   now (or stop if it needs an ADR decision).

4. **Confirm tests.** Every behavior change has tests; a bug fix has a **regression test that fails
   before the fix**. Sync/offline changes → property + scope/idempotency tests; pricing/score
   changes → the **C#≡TS parity vectors**; data access → a **tenant-isolation test**.

5. **Run the local gate — must be green.** Build, lint, unit, **architecture tests**,
   **tenant-isolation**, and integration (Testcontainers). Do **not** weaken any gate to pass
   (no new `IgnoreQueryFilters`, `DateTime.Now`, or raw tenant-bypass; no deleting a failing
   arch-test). If a gate is genuinely wrong, that's a separate PR — stop and say so.

6. **STOP and ask the human** before proceeding if the change touches: authentication/authorization
   or **tenant isolation**; a **public module contract or integration event** signature; a
   **destructive migration** or backfill; or **secrets/infra/deployment** config.

7. **Branch & commit.** Ensure you're on a `type/short-slug` branch off an up-to-date `main` (never
   commit to `main`). Use **Conventional Commit** messages scoped to the module. Never bypass hooks
   or signing.

8. **Open a DRAFT PR** with `gh pr create --draft`, title as a Conventional Commit
   (`feat(visit): geofenced check-in (VIS-01, VIS-02)`), body filled from
   `.github/pull_request_template.md` — spec IDs, scope/non-scope, test evidence, docs updated,
   before/after screenshots for UI, risk/rollback. Claim nothing in the body the diff doesn't
   support: every "verified" should name something you actually ran.

9. **Self-review.** Read your own diff and leave **inline PR comments** on anything non-obvious.

10. **Mark ready only when CI is green** (`gh pr ready`). **Never merge or approve** — leave that to
    the human. Report the PR URL and a one-paragraph summary of what to review and where to start.
