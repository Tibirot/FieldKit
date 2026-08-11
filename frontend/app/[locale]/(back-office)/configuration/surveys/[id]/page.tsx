import { getTranslations, setRequestLocale } from "next-intl/server";

import { SurveyEditor } from "@/components/back-office/survey-editor";
import { resolveLocale } from "@/i18n/locale";

/**
 * One survey form's questions (`AUD-04`, `AUD-07`).
 *
 * The heading is the section's rather than the form's name: the name is an editable control a few
 * lines below, and a title that changed under the caret while somebody renamed it would be a second,
 * lagging copy of the box they are typing into.
 */
export default async function SurveyPage({
  params,
}: {
  params: Promise<{ locale: string; id: string }>;
}) {
  const { locale: requested, id } = await params;
  const locale = resolveLocale(requested);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "Surveys" });

  return (
    <div className="flex max-w-4xl flex-col gap-4">
      <header className="min-w-0">
        <p className="font-mono text-[11.5px] text-muted-foreground">{t("crumb")}</p>
        <h1 className="text-lg font-semibold tracking-tight">{t("editTitle")}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
      </header>
      <SurveyEditor formId={id} />
    </div>
  );
}
