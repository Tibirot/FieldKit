import { setRequestLocale } from "next-intl/server";

import { Order } from "@/components/field/order";
import { resolveLocale } from "@/i18n/locale";

/**
 * `/field/visits/{id}/order` — the order a rep takes at the counter (`ORD-01`) — W11 slice 7.
 *
 * <b>Under the visit rather than beside it.</b> An order belongs to a call: the aggregate ties it to
 * one visit and one outlet, at most one per visit, and the currency comes from the shop's price
 * list. A route that named the order instead would need an id that does not exist until the rep adds
 * their first line — and would let a rep reach an order without the visit that gives it a shop.
 */
export default async function FieldOrderPage({
  params,
}: {
  params: Promise<{ locale: string; visitId: string }>;
}) {
  const { locale, visitId } = await params;
  setRequestLocale(resolveLocale(locale));

  return <Order visitId={visitId} />;
}
