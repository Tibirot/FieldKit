import { getTranslations, setRequestLocale } from "next-intl/server";

import { OutletImport } from "@/components/back-office/outlet-import";
import { resolveLocale } from "@/i18n/locale";

/**
 * Bulk import of the outlet base (`OUT-05`).
 *
 * Upload, check, apply. Correcting a flagged cell in place — the editable grid the spec argues for —
 * is the slice after this one; until then the refused rows come back as a file to fix and re-send,
 * which stays regardless as the escape hatch for files too big to review by eye.
 */
export default async function OutletImportPage({ params }: { params: Promise<{ locale: string }> }) {
  const locale = resolveLocale((await params).locale);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "OutletImport" });

  return (
    <div className="flex max-w-3xl flex-col gap-4">
      <header>
        <p className="font-mono text-[11.5px] text-muted-foreground">{t("crumb")}</p>
        <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
      </header>
      <OutletImport />
    </div>
  );
}
