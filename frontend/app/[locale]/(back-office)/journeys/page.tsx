import { getTranslations, setRequestLocale } from "next-intl/server";

import { Breadcrumb } from "@/components/back-office/breadcrumb";
import { JourneyPlans } from "@/components/back-office/journey-plans";
import { resolveLocale } from "@/i18n/locale";

/**
 * A rep's round: generated, reviewed, and given to them (`JRN-03`, `JRN-04`).
 *
 * The section's landing screen, because it is the one thing the other two exist to feed. Frequency
 * says how often a shop is due and the calendar says how much room there is; this is what comes out,
 * and what it *could not* do is the part a supervisor acts on.
 */
export default async function JourneysPage({ params }: { params: Promise<{ locale: string }> }) {
  const locale = resolveLocale((await params).locale);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "Plans" });

  return (
    <div className="flex max-w-5xl flex-col gap-4">
      <header className="flex flex-wrap items-start gap-3">
        <div className="min-w-0 flex-1">
          <Breadcrumb />
          <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
          <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
        </div>
      </header>
      <JourneyPlans />
    </div>
  );
}
