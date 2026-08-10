import { setRequestLocale } from "next-intl/server";

import { DeviceStatus } from "@/components/field/device-status";
import { resolveLocale } from "@/i18n/locale";

/**
 * `/field` — where the app opens on a phone, until Today's Journey takes the slot in slice 5.
 *
 * Not a placeholder: this is the wireframes' *Sync & reconcile* screen, and it is the one a rep
 * wants when the question is "has my work gone in" rather than "what is my day".
 */
export default async function FieldHomePage({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  setRequestLocale(resolveLocale((await params).locale));

  return <DeviceStatus />;
}
