import { getTranslations, setRequestLocale } from "next-intl/server";

import { PriceListBrowser } from "@/components/back-office/price-list-browser";
import { resolveLocale } from "@/i18n/locale";

/**
 * What products cost, and when (`PRD-03`).
 *
 * A list carries one currency and one half-open window; its prices live behind it, because
 * authoring a list is a different sitting from pricing a catalogue into one. Where a list applies —
 * which channels and outlets it reaches — is a third decision and its own slice.
 */
export default async function PriceListsPage({ params }: { params: Promise<{ locale: string }> }) {
  const locale = resolveLocale((await params).locale);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "PriceLists" });

  return (
    <div className="flex max-w-3xl flex-col gap-4">
      <header>
        <p className="font-mono text-[11.5px] text-muted-foreground">{t("crumb")}</p>
        <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
      </header>
      <PriceListBrowser />
    </div>
  );
}
