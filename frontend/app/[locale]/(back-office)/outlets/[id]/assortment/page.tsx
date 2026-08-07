import { setRequestLocale } from "next-intl/server";

import { OutletAssortment } from "@/components/back-office/outlet-assortment";
import { resolveLocale } from "@/i18n/locale";

/**
 * What this shop sells, and where it departs from its channel (`PRD-02`).
 *
 * Under the outlet rather than under Products, because that is whose decision it is: a channel
 * assortment is authored once by whoever owns the category, and an override is made by whoever
 * looks after this shop. Its own route rather than a section of the outlet form, so a Save here
 * cannot ride along on an edit to an address — the same reasoning that keeps the lifecycle control
 * outside that form.
 *
 * The id is read on the client, like the outlet page itself: this route stays statically rendered
 * and the data is fetched with the caller's token.
 */
export default async function OutletAssortmentPage({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  setRequestLocale(resolveLocale((await params).locale));

  return <OutletAssortment />;
}
