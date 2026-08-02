# Frontend toolchain & the lockfile

> **Status:** ✅ Baseline · **Applies to:** [`frontend/`](../../frontend) · **Enforced by:** the
> `frontend` job in [ci.yml](../../.github/workflows/ci.yml)

Short version: **Node 24 / npm 11, and `frontend/package-lock.json` is regenerated on Linux.**
If CI fails with `Missing: <pkg> from lock file`, jump to [Regenerating the lockfile](#regenerating-the-lockfile).
Writing global CSS? See [Cascade layers](#cascade-layers-global-css) — the rule there is not optional.

## Why this page exists

npm does not produce the same `package-lock.json` on every machine. Two separate causes bit this
repo, and both were mistaken for one "native deps" problem before being measured:

**1. npm major/minor version.** Optional-dependency resolution changed between npm 10 and 11.
Generating the lockfile under one and installing under the other adds or drops entries — the
`@emnapi/*` WASM-fallback packages under `@tailwindcss/oxide-wasm32-wasi` are the ones that move
here.

**2. Operating system — the one that actually breaks the build.** `next` pins
`@swc/helpers@0.5.15` exactly, while `next-intl` accepts a range. npm resolves that overlap
differently per platform: on Linux it nests a second copy (`0.5.23`) under `next-intl`, on Windows
it dedupes to the single hoisted `0.5.15`. Both installs *work*. But a lockfile written on Windows
lacks the nested entry, and `npm ci` on Linux then fails outright:

```
npm error Missing: @swc/helpers@0.5.23 from lock file
```

Pinning the npm version does **not** fix cause 2 — it was verified with identical npm on both
OSes, and the divergence persists. Only generating on Linux does.

> An `overrides` entry forcing one `@swc/helpers` version would collapse the divergence, and was
> rejected: it would change the version **Next itself** runs against to work around a hoisting
> quirk. `@swc/helpers` is a runtime helper library — a silent mismatch there is a worse failure
> than a loud lockfile one.
>
> This repo *does* use `overrides` elsewhere, and the distinction is the point: overriding to fix a
> **CVE with no upstream release** buys a known security fix for a known compatibility risk, and is
> recorded, dated, and test-guarded ([security §6](../architecture/16-security.md#6-application-security-baseline)).
> Overriding for **convenience** — to avoid regenerating a file — trades a loud failure for a silent
> one and buys nothing. Reach for it in the first case only.

## The rules

1. **CI runs `npm ci`, not `npm install`.** `npm install` *rewrites* the lockfile to whatever the
   runner resolves, which means a broken lockfile can never fail the build. `npm ci` installs
   exactly the lockfile and fails loudly on any disagreement. **Do not relax this step to make a
   red build green** — that reintroduces the trap this page documents.
2. **`NODE_VERSION` in [ci.yml](../../.github/workflows/ci.yml), `engines` in
   [package.json](../../frontend/package.json), and `@types/node` move together**, and the lockfile
   is regenerated in the same change. Never bump one alone. `@types/node` in particular **tracks the
   runtime, not the registry** — typechecking against a newer stdlib than the Node you actually run
   accepts code that compiles and then crashes. Its major is held back in
   [dependabot.yml](../../.github/dependabot.yml) for that reason, permanently: it moves when Node
   moves, as part of that change.
3. **Locally, prefer `npm ci`.** It installs exactly the lockfile and — verified on Windows —
   **does not rewrite it**, so it sidesteps this whole problem. `npm install` is fine too when you
   need to add a dependency; just **don't commit the resulting `package-lock.json` diff** unless you
   regenerated it as below, because on Windows it quietly drops the nested `@swc/helpers` entry.
4. **Dependabot is the one sanctioned exception** to rule 3. It resolves on Linux, which removes
   cause 2 — but not cause 1, since its bundled npm major is outside this repo's control. If a
   Dependabot PR fails `npm ci`, that's cause 1: check out its branch, regenerate as below, and
   push. `@dependabot recreate` will not fix it, because the problem isn't a stale branch.

## Cascade layers (global CSS)

**Every ordinary declaration in [`app/globals.css`](../../frontend/app/globals.css) must sit inside
`@layer base`.** Custom properties (`:root`, `.dark`, `@theme`) are the exception and stay
unlayered — utilities *consume* tokens, so they cannot conflict.

The reason is a cascade rule that reverses the intuition specificity trains: **unlayered styles beat
layered ones**, always, no matter how specific the layered selector is. Tailwind v4 emits utilities
into `@layer utilities`, so a single bare rule in globals.css outranks the entire utility system.

This is not a theoretical hazard — it shipped. The Tailwind v4 slice carried the `create-next-app`
boilerplate reset unchanged:

```css
* { box-sizing: border-box; padding: 0; margin: 0; }   /* ← unlayered */
```

Result: **every padding and margin utility in the app silently did nothing.** `<main class="… p-6">`
computed to `padding: 0px`; shadcn buttons lost their horizontal padding, so cards rendered
edge-to-edge on mobile and button labels were clipped flush against the edges. Nothing failed — not
the build, not the types, not the tests. It looked like a design that had simply never been
finished, which is why it survived a whole slice.

Two things keep it from coming back:

- `app/globals.test.ts` parses globals.css and fails the build on any non-custom declaration outside
  a layer.
- Before adding a reset, check **Tailwind's preflight** — it already emits
  `box-sizing: border-box; margin: 0; padding: 0; border: 0 solid` on `*, ::before, ::after,
  ::backdrop` inside `@layer base`. The reset above was pure duplication, which is why it was
  deleted rather than wrapped.

Verify styling changes in a real browser at **375px and desktop**, in both locales, against the
[wireframes](../ux/README.md). A computed-style check (`getComputedStyle(el).padding`) catches this
class of bug in seconds; a screenshot alone can be mistaken for an unfinished design.

## Regenerating the lockfile

Any time you add, remove, or bump a dependency. Requires Docker; the image major matches
`NODE_VERSION` in CI:

```bash
docker run --rm -v "$(pwd)/frontend:/app" -w /app node:24 \
  npm install --package-lock-only --no-audit --no-fund
```

On Windows PowerShell:

```powershell
docker run --rm -v "${PWD}\frontend:/app" -w /app node:24 npm install --package-lock-only --no-audit --no-fund
```

Then verify it strictly installs the way CI will, before committing:

```bash
docker run --rm -v "$(pwd)/frontend:/src:ro" -w /build node:24 \
  sh -c 'cp -r /src/. /build && rm -rf node_modules .next && npm ci --no-audit --no-fund'
```

## Verifying a change to this setup

Reproduce the failure before trusting a fix — this was originally misdiagnosed from a plausible
symptom rather than measured:

```bash
# 1. mangle the lockfile the way a Windows `npm install` does
npm install --package-lock-only          # run on Windows
# 2. confirm CI would now fail
docker run --rm -v "$(pwd)/frontend:/src:ro" -w /build node:24 \
  sh -c 'cp -r /src/. /build && rm -rf node_modules .next && npm ci'   # expect: Missing: … from lock file
# 3. regenerate per above, re-run step 2, expect success
```
