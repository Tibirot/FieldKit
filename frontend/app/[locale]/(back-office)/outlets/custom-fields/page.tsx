import { getTranslations, setRequestLocale } from "next-intl/server";

import { FieldDefinitionBrowser } from "@/components/back-office/field-definition-browser";
import { resolveLocale } from "@/i18n/locale";

/**
 * The outlet custom-field catalogue (`CFG-01`).
 *
 * Under `/outlets` rather than in its own admin section because the catalogue is per-entity: what
 * this screen edits is what the outlet form renders and what the import validates, and none of it
 * means anything away from outlets. Products bring their own in W6.
 */
export default async function CustomFieldsPage({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  const locale = resolveLocale((await params).locale);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "CustomFields" });

  return (
    <div className="flex max-w-3xl flex-col gap-4">
      <header>
        <p className="font-mono text-[11.5px] text-muted-foreground">{t("crumb")}</p>
        <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
      </header>
      <FieldDefinitionBrowser entity="Outlet" />
    </div>
  );
}
