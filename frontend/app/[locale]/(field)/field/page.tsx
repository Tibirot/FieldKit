import { setRequestLocale } from "next-intl/server";

import { TodaysJourney } from "@/components/field/todays-journey";
import { resolveLocale } from "@/i18n/locale";

/**
 * `/field` — where the app opens on a phone (`JRN-05`).
 *
 * Today's Journey took this slot from the device screen in W9 slice 5. The order matters more than
 * it looks: what a rep wants on opening the app is *where am I going*, and the sync state they were
 * getting instead is already one glance away in the chrome above.
 */
export default async function FieldHomePage({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  setRequestLocale(resolveLocale((await params).locale));

  return <TodaysJourney />;
}
