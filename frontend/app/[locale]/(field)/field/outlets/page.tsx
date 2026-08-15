import { setRequestLocale } from "next-intl/server";

import { FieldOutlets } from "@/components/field/outlets";
import { resolveLocale } from "@/i18n/locale";

/**
 * `/field/outlets` — every shop the rep covers (`A4`, `JRN-06`) — W12½ slice 8a.
 *
 * The index of a segment that has had a detail route since W9 and no list: `/field/outlets/{id}` was
 * reachable only from today's round or from the unplanned-call picker, which deliberately hides
 * every shop already planned. A rep looking one up out of order had nowhere to start.
 */
export default async function FieldOutletsPage({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  setRequestLocale(resolveLocale((await params).locale));

  return <FieldOutlets />;
}
