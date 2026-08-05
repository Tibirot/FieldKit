import { getTranslations, setRequestLocale } from "next-intl/server";

import { OutletForm } from "@/components/back-office/outlet-form";
import { resolveLocale } from "@/i18n/locale";

/** Create an outlet (`OUT-01`, `OUT-02`). */
export default async function NewOutletPage({ params }: { params: Promise<{ locale: string }> }) {
  const locale = resolveLocale((await params).locale);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "OutletForm" });

  return (
    <div className="flex flex-col gap-4">
      <header>
        <p className="font-mono text-[11.5px] text-muted-foreground">{t("crumbNew")}</p>
        <h1 className="text-lg font-semibold tracking-tight">{t("titleNew")}</h1>
      </header>
      <OutletForm />
    </div>
  );
}
