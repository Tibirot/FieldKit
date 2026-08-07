import { setRequestLocale } from "next-intl/server";

import { PromotionTargets } from "@/components/back-office/promotion-targets";
import { resolveLocale } from "@/i18n/locale";

/**
 * What a promotion discounts (`PRD-05`).
 *
 * Its own route rather than a section of the authoring form: what a deal *is* and what it *applies
 * to* are decided at different times, and one Save covering both would let a stray tick change which
 * products are discounted as a side effect of correcting a percentage.
 */
export default async function PromotionTargetsPage({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  setRequestLocale(resolveLocale((await params).locale));

  return <PromotionTargets />;
}
