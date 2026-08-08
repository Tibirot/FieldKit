import { setRequestLocale } from "next-intl/server";

import { PromotionTiers } from "@/components/back-office/promotion-tiers";
import { resolveLocale } from "@/i18n/locale";

/**
 * The thresholds a tiered promotion discounts by (`PRD-05`).
 *
 * Only `VolumeTiered` has them — a flat promotion with tiers would carry two discounts and no rule
 * saying which applies. The route exists for every promotion because ids are typeable; the screen
 * says which type it found rather than offering an editor that would refuse everything.
 */
export default async function PromotionTiersPage({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  setRequestLocale(resolveLocale((await params).locale));

  return <PromotionTiers />;
}
