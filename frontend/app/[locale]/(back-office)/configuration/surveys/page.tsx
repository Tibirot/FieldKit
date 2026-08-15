import { getTranslations, setRequestLocale } from "next-intl/server";

import { SurveyBrowser } from "@/components/back-office/survey-browser";
import { resolveLocale } from "@/i18n/locale";

/**
 * The tenant's survey forms (`AUD-04`, `CFG-04`).
 *
 * The way into the editor slice 9a built, which until now was reachable only by typing its address.
 */
export default async function SurveysPage({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  const locale = resolveLocale((await params).locale);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "SurveyList" });

  return (
    <div className="flex max-w-4xl flex-col gap-4">
      <header className="min-w-0">
        <p className="font-mono text-[11.5px] text-muted-foreground">{t("crumb")}</p>
        <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
      </header>
      <SurveyBrowser />
    </div>
  );
}
