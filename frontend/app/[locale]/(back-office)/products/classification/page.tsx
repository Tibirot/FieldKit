import { getTranslations, setRequestLocale } from "next-intl/server";

import { Breadcrumb } from "@/components/back-office/breadcrumb";
import { ClassificationBrowser } from "@/components/back-office/classification-browser";
import { resolveLocale } from "@/i18n/locale";

/**
 * How a tenant classifies its products (`PRD-01`).
 *
 * **Three vocabularies on one page**, because they are one job — saying how the catalogue is
 * organised — done once during onboarding and rarely revisited. Its own route rather than a section
 * of the product list for the same reason `/outlets/channels` is one: the catalogue is a daily
 * screen and this is not.
 *
 * A brand, a category and a tax class each mean something different to the rest of the system — a
 * category scopes promotions and share-of-shelf, a tax class carries the rates — but a tenant sets
 * all three up in the same sitting, and three routes would make that three trips.
 */
export default async function ClassificationPage({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  const locale = resolveLocale((await params).locale);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "Classification" });

  return (
    <div className="flex max-w-3xl flex-col gap-6">
      <header>
        <Breadcrumb />
        <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
      </header>
      <ClassificationBrowser />
    </div>
  );
}
