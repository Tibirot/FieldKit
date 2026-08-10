import { setRequestLocale } from "next-intl/server";

import { DeviceStatus } from "@/components/field/device-status";
import { resolveLocale } from "@/i18n/locale";

/**
 * `/field/device` — the wireframes' *Sync & reconcile* screen.
 *
 * It opened the app until Today's Journey arrived (W9 slice 5) and moved here rather than being
 * deleted: "has my work gone in" is a question a rep genuinely asks, just not the first one. The
 * indicator in the chrome links to it.
 */
export default async function FieldDevicePage({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  setRequestLocale(resolveLocale((await params).locale));

  return <DeviceStatus />;
}
