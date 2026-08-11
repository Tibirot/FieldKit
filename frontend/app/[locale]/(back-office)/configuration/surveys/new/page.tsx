import { getTranslations, setRequestLocale } from "next-intl/server";

import { ConfigurationActions } from "@/components/back-office/configuration-actions";
import { SurveyEditor } from "@/components/back-office/survey-editor";
import { resolveLocale } from "@/i18n/locale";

/**
 * A new survey form (`AUD-04`, `AUD-07`).
 *
 * Its own route rather than a mode of the editor's, the way `/outlets/new` is: a static segment wins
 * over `[id]` in Next's matcher, so `new` cannot be mistaken for a form whose id happens to be that
 * word. The editor is the same component — a form cannot be created empty (the server refuses one
 * with no questions), so there is nothing a create-only screen could offer that this does not.
 */
export default async function NewSurveyPage({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  const locale = resolveLocale((await params).locale);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "Surveys" });

  return (
    <div className="flex max-w-4xl flex-col gap-4">
      <header className="min-w-0">
        <p className="font-mono text-[11.5px] text-muted-foreground">{t("crumb")}</p>
        <h1 className="text-lg font-semibold tracking-tight">{t("newTitle")}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
      </header>
      <ConfigurationActions />
      <SurveyEditor formId={null} />
    </div>
  );
}
