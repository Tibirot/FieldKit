import { setRequestLocale } from "next-intl/server";

import { CheckIn } from "@/components/field/check-in";
import { resolveLocale } from "@/i18n/locale";

/**
 * `/field/outlets/{id}` — the shop a rep is standing outside (`VIS-01`, `VIS-02`).
 *
 * <b>The outlet id is in the path and the planned call is in the query, which is the right way
 * round.</b> The shop is what the screen is *about* — a rep can reach it from an unplanned call, a
 * search, or a link — and the call it answers is context the journey supplies when it has one.
 * `JRN-06` is explicit that an unplanned visit is ordinary, so a route that demanded a call id would
 * have made the ordinary case unreachable.
 */
export default async function FieldOutletPage({
  params,
  searchParams,
}: {
  params: Promise<{ locale: string; outletId: string }>;
  searchParams: Promise<{ call?: string }>;
}) {
  const { locale, outletId } = await params;
  setRequestLocale(resolveLocale(locale));

  const { call } = await searchParams;

  return <CheckIn outletId={outletId} plannedVisitId={call} />;
}
