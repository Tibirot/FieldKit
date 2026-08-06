# ADR-0010: Internationalization (currency & language)

- **Status:** Accepted
- **Date:** 2026-08
- **Deciders:** Tiberiu Socea
- **Related:** decision [A3](../../product/decisions-and-assumptions.md#a3--internationalization-full-multi-currency--multi-language-ui),
  [SharedKernel / domain model](../11-domain-model.md)
- **Extended by:** [ADR-0012](0012-server-message-localization.md) — this ADR settles the *UI*
  catalogs and leaves server-produced messages unaddressed; 0012 closes that gap. Nothing here is
  reversed.

## Context

FieldKit targets operations across **the US and Europe** ([A3](../../product/decisions-and-assumptions.md#a3--internationalization-full-multi-currency--multi-language-ui)):
multiple currencies, languages, and timezones. i18n done late is a pervasive, painful retrofit
(every price, date, and label), so the primitives are decided up front.

## Decision

**Build international from the first commit.**

### Money — a first-class value object
- A `Money(amount, currency)` value object in `SharedKernel` is used **everywhere** money appears
  ([domain model](../11-domain-model.md)).
- **No implicit cross-currency arithmetic** — operations require the same currency; a price list
  carries one currency and everything derived stays in it ([Pricing BR-PRD-1](../../product/13-products-and-pricing.md#5-business-rules)).
- Decimal (never float) amounts; currency is ISO-4217.

### Time — UTC everywhere, display in context
- All timestamps stored **UTC**; an injected **`IClock`** is the only time source (enforced by
  [architecture test AT-7](../10-module-boundaries.md#5-enforcement--architecture-tests)).
- Display converts to the **user's timezone**; a visit's business "day" resolves in the **outlet's**
  timezone.

### Language — message catalogs
- UI localized via **`next-intl`**: message catalogs + locale-aware number/date/currency
  formatting. Launch languages **English + Romanian** (`en`, `ro`).
- **Locale routing** is an always-present URL prefix (`/en/…`, `/ro/…`), negotiated by a Next
  proxy (URL → `NEXT_LOCALE` cookie → `Accept-Language` → default). Explicit URLs keep the PWA
  app-shell cache unambiguous — a cached response can never be for the wrong language.
- Locale + timezone are **mandatory on the user profile** ([IAM BR-IAM-5](../../product/10-identity-and-access.md#5-business-rules)).
  Until IAM lands (W3), the request timezone defaults to **UTC**.
- **Localized reference data** (e.g. product names per language) via translation tables —
  **Could-have**, Phase 4, so core delivery isn't gated on translation content.

## Options considered

| Option | Verdict | Why |
|---|---|---|
| Single locale (defer i18n) | Rejected | Contradicts the US+EU reality; retrofitting i18n later is far costlier. |
| Multi-currency + timezone, English UI only | Reasonable, rejected | Covers the hard parts, but [A3](../../product/decisions-and-assumptions.md#a3--internationalization-full-multi-currency--multi-language-ui) chose full i18n incl. UI language. |
| **Full i18n: Money VO + UTC/tz + next-intl catalogs** | **Chosen** | Matches the target markets; primitives in place from day one. |

## Consequences

**Positive**
- Currency and time bugs are structurally prevented (typed `Money`, UTC + `IClock`), not
  patched later.
- Adding a language is a **content** task (a catalog), not an engineering change — and that claim
  is *enforced*, not asserted: see the catalog gate below.

**Negative / costs**
- `Money` discipline everywhere (no raw `decimal` prices) — enforced by review/domain model.
- Translation content is ongoing upkeep; scoped down via the Could-have on reference-data
  localization.
- Slightly more front-end plumbing (locale routing/proxy) from the start.

## Implementation (front end)

| Path | Role |
|---|---|
| `frontend/i18n/routing.ts` | The locale set + prefix strategy — **the only place a language is registered**. |
| `frontend/i18n/request.ts` | Per-request config: catalog import, `timeZone`. |
| `frontend/i18n/locale.ts` | Narrows the user-controlled `[locale]` param; unknown locales 404. |
| `frontend/i18n/navigation.ts` | Locale-aware `Link` / router — use these, never `next/link` directly. |
| `frontend/proxy.ts` | Locale negotiation + redirect (Next 16's successor to `middleware.ts`). |
| `frontend/messages/<locale>.json` | The catalogs. |
| `frontend/global.d.ts` | Types `t()` off the English catalog, so a bad key is a **compile** error. |

**The catalog gate** — `frontend/i18n/catalogs.test.ts` fails the build when catalogs drift: a key
present in one language only, a blank translation, a renamed ICU placeholder, malformed ICU, or
plural categories that don't exist in the target language (English has `one`/`other`; Romanian
also has `few`). This is what makes "adding a language is a content task" true rather than hopeful.
