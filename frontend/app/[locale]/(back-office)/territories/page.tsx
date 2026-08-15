import { getTranslations, setRequestLocale } from "next-intl/server";

import { Breadcrumb } from "@/components/back-office/breadcrumb";
import { OrgUnitBrowser } from "@/components/back-office/org-unit-browser";
import { TerritoryBrowser } from "@/components/back-office/territory-browser";
import { resolveLocale } from "@/i18n/locale";

/**
 * Territories (`ORG-03`).
 *
 * **The sales hierarchy is above them**, because a territory hangs off an org unit and a workspace
 * with no hierarchy cannot have one. Two sections rather than two routes: modelling an organisation
 * is one sitting, and making an admin navigate between the level they are creating and the thing
 * that needs it is a worse screen than a longer one.
 *
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
        <Breadcrumb />
        <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
      </header>
      <OrgUnitBrowser />

      <hr className="border-border" />

      <TerritoryBrowser />
    </div>
  );
}
