import { getTranslations, setRequestLocale } from "next-intl/server";

import { Breadcrumb } from "@/components/back-office/breadcrumb";
import { VisitList } from "@/components/back-office/visit-list";
import { resolveLocale } from "@/i18n/locale";

/**
 * What the field recorded (`VIS-10`).
 *
 * The rail advertised this as `W9` for four weeks, then as `W12`; this is it. Read-only, because a
 * checked-out visit is sealed (`BR-VIS-4`) and every write path in the module already refuses one.
 */
export default async function VisitsPage({ params }: { params: Promise<{ locale: string }> }) {
  const locale = resolveLocale((await params).locale);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "Visits" });

  return (
    <div className="flex max-w-5xl flex-col gap-4">
      <header className="flex flex-wrap items-start gap-3">
        <div className="min-w-0 flex-1">
          <Breadcrumb />
          <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
          <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
        </div>
      </header>
      <VisitList />
    </div>
  );
}
