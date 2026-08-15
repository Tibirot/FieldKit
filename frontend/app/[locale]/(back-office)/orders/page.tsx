import { getTranslations, setRequestLocale } from "next-intl/server";

import { Breadcrumb } from "@/components/back-office/breadcrumb";
import { OrderQueue } from "@/components/back-office/order-queue";
import { resolveLocale } from "@/i18n/locale";

/**
 * The orders a supervisor works through (`ORD-09`).
 *
 * The rail's `W11` badge, re-badged to `W12` and now built. Read-only for this slice: the rejection
 * path exists end to end on the server and gets its control in 6b — what is here already is the
 * rejection itself, because an order that was refused and does not say why is the thing this screen
 * exists to prevent.
 */
export default async function OrdersPage({ params }: { params: Promise<{ locale: string }> }) {
  const locale = resolveLocale((await params).locale);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "Orders" });

  return (
    <div className="flex max-w-5xl flex-col gap-4">
      <header className="flex flex-wrap items-start gap-3">
        <div className="min-w-0 flex-1">
          <Breadcrumb />
          <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
          <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
        </div>
      </header>
      <OrderQueue />
    </div>
  );
}
