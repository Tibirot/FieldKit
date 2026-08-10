import { setRequestLocale } from "next-intl/server";

import { Visit } from "@/components/field/visit";
import { resolveLocale } from "@/i18n/locale";

/**
 * `/field/visits/{id}` — the visit a rep is working (`VIS-03`, `VIS-06`).
 *
 * <b>The id is the device's own, and it never round-trips through a server.</b> It was minted at
 * check-in and is what `CapturedVisit.visitId` will carry, so this URL is stable from the moment the
 * visit exists — including for the whole of a day with no signal, which is the point.
 */
export default async function FieldVisitPage({
  params,
}: {
  params: Promise<{ locale: string; visitId: string }>;
}) {
  const { locale, visitId } = await params;
  setRequestLocale(resolveLocale(locale));

  return <Visit visitId={visitId} />;
}
