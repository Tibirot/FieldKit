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

/** Every refusal in an error, in the reader's language, in the order the API listed them. */
export function refusalTexts(
  t: RefusalTranslator,
  problems: readonly FieldProblem[],
): string[] {
  return problems.map((problem) => refusalText(t, problem));
}
