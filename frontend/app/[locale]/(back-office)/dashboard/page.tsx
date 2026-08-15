import { getTranslations, setRequestLocale } from "next-intl/server";

import { Breadcrumb } from "@/components/back-office/breadcrumb";
import { Dashboard } from "@/components/back-office/dashboard";
import { resolveLocale } from "@/i18n/locale";

/**
 * How the field is doing (`AUD-09`, `JRN-10`, `ORD-09`, `VIS-10`).
 *
 * The rail has advertised this screen since W1 and re-badged it twice. It is the first back-office
 * page that reads across every module — coverage from Journey and Visit, strike rate from Visit,
 * perfect store from Audit, order value from Order — and it does it in one request, because the
 * composition lives on the server where the four contracts are.
 */
export default async function DashboardPage({ params }: { params: Promise<{ locale: string }> }) {
  const locale = resolveLocale((await params).locale);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "Dashboard" });

  return (
    <div className="flex max-w-5xl flex-col gap-4">
      <header className="flex flex-wrap items-start gap-3">
        <div className="min-w-0 flex-1">
          <Breadcrumb />
          <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
          <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
        </div>
      </header>
      <Dashboard />
    </div>
  );
}
