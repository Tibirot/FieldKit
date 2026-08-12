import { setRequestLocale } from "next-intl/server";

import { Audit } from "@/components/field/audit";
import { resolveLocale } from "@/i18n/locale";

/**
 * `/field/visits/{id}/audit` — the shelf as the rep found it (`AUD-01`) — W11 slice 9a.
 *
 * <b>Under the visit</b>, for the reason the order route gives and one of its own: `BR-AUD-6` ties
 * an audit to a visit and seals the two together, and the MSL the rep answers for is the *outlet's*
 * — so a route that named the audit would need an id that does not exist until the first tap, and
 * would let a rep reach a shelf with no shop behind it.
 */
export default async function FieldAuditPage({
  params,
}: {
  params: Promise<{ locale: string; visitId: string }>;
}) {
  const { locale, visitId } = await params;
  setRequestLocale(resolveLocale(locale));

  return <Audit visitId={visitId} />;
}
