import type { useTranslations } from "next-intl";

import type { FieldProblem } from "@/lib/api/client";

/** The `Refusals` translator, as `useTranslations("Refusals")` returns it. */
export type RefusalTranslator = ReturnType<typeof useTranslations<"Refusals">>;

/** The literal key names that translator accepts — every code the catalogue actually has. */
type RefusalKey = Parameters<RefusalTranslator>[0];

/**
 * One refusal, in the reader's language ([ADR-0012](../../../docs/architecture/adr/0012-server-message-localization.md) stage 2).
 *
 * **The fallback is the design, not a safety net.** A code this catalogue has no entry for renders
 * as the server's English sentence — a correct sentence a user can act on, rather than
 * `product.priceList.nameTaken`. That is what lets modules migrate one at a time, and what stops a
 * new server rule from showing a raw dotted name the day it ships and before anyone translates it.
 *
 * `args` are passed through as ICU values. A catalogue entry naming a placeholder the server did not
 * send would throw inside `next-intl`, so the entry and the code's `Args` have to agree — the
 * coupling ADR-0012 named as a cost, and the reason stage 4 wants a test that walks the codes the
 * server can emit.
 */
export function refusalText(t: RefusalTranslator, problem: FieldProblem): string {
  // Cast because the key is a string the *server* chose, and TypeScript can only accept the
  // catalogue's literal names. `t.has` is what makes it safe — a code with no entry never reaches
  // the call below, which is the same shape `formPath` uses in the outlet form.
  const code = problem.code as RefusalKey | undefined | null;

  if (!code || !t.has(code)) return problem.message;

  return t(code, problem.args ?? {});
}

/**
 * A refusal the device stored rather than one it just received — W11½ R5.
 *
 * <b>The same rule as {@link refusalText}, over a different shape.</b> An HTTP refusal arrives as a
 * `FieldProblem` and is read once; a *sync* refusal is written to the outbox by `markRejected` and
 * read minutes or hours later, by which time the request is long gone. Only the code and the
 * server's sentence survive, and that turns out to be exactly what is needed.
 *
 * <b>No `args`, and that is the honest limitation.</b> `markRejected` never stored them, so a
 * catalogue entry naming a placeholder has nothing to fill it with — and `t.has` cannot tell the two
 * kinds of entry apart. **`next-intl` does not throw on a missing value**, which is the trap: it
 * reports the error and returns *the key path*, so the guard cannot be a `try`/`catch` and a screen
 * that trusted `t.has` would print `Refusals.journey.plan.windowTooLong` at a rep — the exact failure
 * [ADR-0012](../../../docs/architecture/adr/0012-server-message-localization.md) exists to prevent.
 *
 * So the template is inspected instead. An entry with no placeholder is safe to format; anything
 * else falls back to the server's English, which is ADR-0012's design rather than a shortfall — a
 * correct sentence a rep can act on beats `journey.plan.noneForDate`.
 *
 * Returns `undefined` when there is nothing to say, so a caller renders no empty box.
 */
export function storedRefusalText(
  t: RefusalTranslator,
  refusal: { code?: string; detail?: string },
): string | undefined {
  // Cast because the key is a string the *server* chose, and TypeScript can only accept the
  // catalogue's literal names — the same shape `refusalText` uses above.
  const code = refusal.code as RefusalKey | undefined;

  if (code && t.has(code)) {
    const template: unknown = t.raw(code);

    // A brace is enough: ICU placeholders, plurals and selects all open with one, and every entry
    // that has none formats to itself. Erring towards the server's sentence costs a translation;
    // erring the other way prints a dotted key.
    if (typeof template === "string" && !template.includes("{")) return t(code);
  }

  return refusal.detail || undefined;
}

/** Every refusal in an error, in the reader's language, in the order the API listed them. */
export function refusalTexts(
  t: RefusalTranslator,
  problems: readonly FieldProblem[],
): string[] {
  return problems.map((problem) => refusalText(t, problem));
}
