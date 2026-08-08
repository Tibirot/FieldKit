import { setRequestLocale } from "next-intl/server";

import { PromotionScope } from "@/components/back-office/promotion-scope";
import { resolveLocale } from "@/i18n/locale";

/**
 * Where a promotion runs (`PRD-05`).
 *
 * The last thing a deal needs before it does anything: type, value, targets and window describe a
 * rule, and this says who it happens to. Its own route for the same reason a price list's scope is —
 * what a deal *is*, what it *discounts* and where it *runs* are three decisions made at three
 * different times.
 */
export default async function PromotionScopePage({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  setRequestLocale(resolveLocale((await params).locale));

  return <PromotionScope />;
}
