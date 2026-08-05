import { getTranslations, setRequestLocale } from "next-intl/server";

import { TerritoryBrowser } from "@/components/back-office/territory-browser";
import { resolveLocale } from "@/i18n/locale";

/**
 * Territories (`ORG-03`).
 *
 * The list and its outlet counts. **Rep assignment** — who covers a territory and over what dates
 * (`ORG-04`) — is the slice after this one, and lands in the detail panel the wireframe draws.
 * Coverage % and the channel-mix bars are reporting, deferred to W12 with the dashboard's read side
 * ([UX build scope](../../../../../docs/ux/README.md)).
 */
export default async function TerritoriesPage({ params }: { params: Promise<{ locale: string }> }) {
  const locale = resolveLocale((await params).locale);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "Territories" });

  return (
    <div className="flex flex-col gap-4">
      <header>
        <p className="font-mono text-[11.5px] text-muted-foreground">{t("crumb")}</p>
        <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
      </header>
      <TerritoryBrowser />
    </div>
  );
}
