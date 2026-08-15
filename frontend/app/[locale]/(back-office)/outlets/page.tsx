import { getTranslations, setRequestLocale } from "next-intl/server";

import { Breadcrumb } from "@/components/back-office/breadcrumb";
import { NewOutletButton } from "@/components/back-office/new-outlet-button";
import { OutletBrowser } from "@/components/back-office/outlet-browser";
import { resolveLocale } from "@/i18n/locale";

/**
 * The outlet base (`OUT-01`, `OUT-03`, `OUT-04`).
 *
 * The whole vertical in one table: a token minted by a tenant's realm, an API that validated it, a
 * tenant-scoped query, and a territory resolved across a module boundary (`ORG-05`). Filters,
 * create/edit and the import screen have landed on top of it since.
 */
export default async function OutletsPage({ params }: { params: Promise<{ locale: string }> }) {
  const locale = resolveLocale((await params).locale);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "Outlets" });

  return (
    <div className="flex flex-col gap-4">
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <Breadcrumb />
          <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
        </div>
        <NewOutletButton />
      </header>
      <OutletBrowser />
    </div>
  );
}
