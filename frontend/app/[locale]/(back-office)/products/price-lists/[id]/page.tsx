import { setRequestLocale } from "next-intl/server";

import { PriceListPrices } from "@/components/back-office/price-list-prices";
import { resolveLocale } from "@/i18n/locale";

/**
 * The prices in one list (`PRD-03`).
 *
 * The id is read on the client, like the outlet detail route: this page stays statically rendered
 * and the list is fetched with the caller's token.
 */
export default async function PriceListPage({ params }: { params: Promise<{ locale: string }> }) {
  setRequestLocale(resolveLocale((await params).locale));

  return <PriceListPrices />;
}
