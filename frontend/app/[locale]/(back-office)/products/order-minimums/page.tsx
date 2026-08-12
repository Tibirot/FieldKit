import { setRequestLocale } from "next-intl/server";

import { OrderMinimums } from "@/components/back-office/order-minimums";
import { resolveLocale } from "@/i18n/locale";

/**
 * The smallest order a shop may place (`ORD-06`, `BR-ORD-5`).
 *
 * Its own route rather than a section on a price list, because a minimum is not a price: it is
 * tenant-wide, it outlives any one list, and it is the only rule in the module a **rep** is refused
 * by. Hanging it off a list would make withdrawing one a side effect of retiring the other.
 */
export default async function OrderMinimumsPage({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  setRequestLocale(resolveLocale((await params).locale));

  return <OrderMinimums />;
}
