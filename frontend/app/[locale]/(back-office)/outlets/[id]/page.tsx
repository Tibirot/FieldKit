import { setRequestLocale } from "next-intl/server";

import { OutletEditor } from "@/components/back-office/outlet-editor";
import { resolveLocale } from "@/i18n/locale";

/**
 * Edit one outlet (`OUT-01`, `OUT-02`).
 *
 * The id is read on the client rather than from `params` here, because this route is statically
 * rendered like the rest of the shell and the outlet itself is fetched with the caller's token.
 * Awaiting `params` for a value the client also needs would make the page dynamic for nothing.
 */
export default async function OutletPage({ params }: { params: Promise<{ locale: string }> }) {
  setRequestLocale(resolveLocale((await params).locale));

  return <OutletEditor />;
}
