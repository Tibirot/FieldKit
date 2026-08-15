import { getTranslations, setRequestLocale } from "next-intl/server";

import { Breadcrumb } from "@/components/back-office/breadcrumb";
import { VisitDetail } from "@/components/back-office/visit-detail";
import { resolveLocale } from "@/i18n/locale";

/**
 * One visit, reviewed (`VIS-10`, `AUD-09`).
 *
 * The screen the list exists to reach: what the rep was asked to do, what they did, and the audit
 * beneath it with its score and pillars. Read-only — a checked-out visit is sealed (`BR-VIS-4`) and
 * an audit is append-only (`BR-AUD-6`).
 */
export default async function VisitPage({
  params,
}: {
  params: Promise<{ locale: string; id: string }>;
}) {
  const { locale: requested, id } = await params;
  const locale = resolveLocale(requested);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "VisitDetail" });

  return (
    <div className="flex max-w-5xl flex-col gap-4">
      <header className="flex flex-wrap items-start gap-3">
        <div className="min-w-0 flex-1">
          <Breadcrumb leaf={t("crumb")} />
          <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
          <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
        </div>
      </header>
      <VisitDetail visitId={id} />
    </div>
  );
}
