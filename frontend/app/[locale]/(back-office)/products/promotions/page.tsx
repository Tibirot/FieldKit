import { getTranslations, setRequestLocale } from "next-intl/server";

import { Breadcrumb } from "@/components/back-office/breadcrumb";
import { PromotionBrowser } from "@/components/back-office/promotion-browser";
import { resolveLocale } from "@/i18n/locale";

/**
 * The deals a tenant runs (`PRD-05`).
 *
 * Four types (`B1`), each with its own half-open window and a priority that decides which one wins
 * when two apply. What a promotion discounts, and where it reaches, are separate aggregates and
 * separate slices — a promotion authored here exists and discounts nobody until they land.
 */
export default async function PromotionsPage({ params }: { params: Promise<{ locale: string }> }) {
  const locale = resolveLocale((await params).locale);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "Promotions" });

  return (
    <div className="flex max-w-3xl flex-col gap-4">
      <header>
        <Breadcrumb />
        <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
      </header>
      <PromotionBrowser />
    </div>
  );
}
