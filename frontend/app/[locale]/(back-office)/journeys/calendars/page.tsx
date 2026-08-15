import { getTranslations, setRequestLocale } from "next-intl/server";

import { Breadcrumb } from "@/components/back-office/breadcrumb";
import { WorkingCalendars } from "@/components/back-office/working-calendars";
import { resolveLocale } from "@/i18n/locale";

/**
 * When a rep works, and what the tenant does not (`JRN-02`).
 *
 * The second of the two things generation reads. Frequency says how often a shop is due; this says
 * how much room there is to call on it — and the gap between the two is what a plan's shortfalls
 * are made of.
 */
export default async function CalendarsPage({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  const locale = resolveLocale((await params).locale);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "Calendars" });

  return (
    <div className="flex max-w-4xl flex-col gap-4">
      <header className="flex flex-wrap items-start gap-3">
        <div className="min-w-0 flex-1">
          <Breadcrumb />
          <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
          <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
        </div>
      </header>
      <WorkingCalendars />
    </div>
  );
}
