import { setRequestLocale } from "next-intl/server";

import { PriceListScope } from "@/components/back-office/price-list-scope";
import { resolveLocale } from "@/i18n/locale";

/**
 * Where a price list applies (`PRD-03`).
 *
 * Its own route rather than a section beside the prices: pricing a catalogue and deciding which
 * shops pay those prices are different decisions, and one Save covering both would let a stray tick
 * change what an outlet is charged as a side effect of correcting an amount.
 */
export default async function PriceListScopePage({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  setRequestLocale(resolveLocale((await params).locale));

  return <PriceListScope />;
}
