import { getTranslations, setRequestLocale } from "next-intl/server";

import { AssortmentEditor } from "@/components/back-office/assortment-editor";
import { resolveLocale } from "@/i18n/locale";

/**
 * Which products belong in which outlets (`PRD-02`).
 *
 * Authored per **channel** — a decision about a kind of shop rather than about one shop — and read
 * per outlet, where a rep's suggested list and an audit's availability checks both come from it.
 * An outlet's departures from its channel are overrides, which are a different decision made by
 * different people, and get their own screen.
 */
export default async function AssortmentsPage({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  const locale = resolveLocale((await params).locale);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "Assortments" });

  return (
    <div className="flex max-w-4xl flex-col gap-4">
      <header>
        <p className="font-mono text-[11.5px] text-muted-foreground">{t("crumb")}</p>
        <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
      </header>
      <AssortmentEditor />
    </div>
  );
}
