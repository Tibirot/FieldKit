import { getTranslations, setRequestLocale } from "next-intl/server";

import { Plus } from "lucide-react";

import { OutletBrowser } from "@/components/back-office/outlet-browser";
import { Button } from "@/components/ui/button";
import { Link } from "@/i18n/navigation";
import { resolveLocale } from "@/i18n/locale";

/**
 * The outlet base (`OUT-01`, `OUT-03`, `OUT-04`).
 *
 * Read-only for now — filters, create/edit with the custom-field form, and the import screen are the
 * slices after this one. What it proves is the whole vertical: a token minted by a tenant's realm,
 * an API that validated it, a tenant-scoped query, and a territory resolved across a module boundary
 * (`ORG-05`) all arriving in one table.
 */
export default async function OutletsPage({ params }: { params: Promise<{ locale: string }> }) {
  const locale = resolveLocale((await params).locale);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "Outlets" });

  return (
    <div className="flex flex-col gap-4">
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <p className="font-mono text-[11.5px] text-muted-foreground">{t("crumb")}</p>
          <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
        </div>
        <Button render={<Link href="/outlets/new" />} nativeButton={false} size="sm">
          <Plus className="size-4" />
          {t("newOutlet")}
        </Button>
      </header>
      <OutletBrowser />
    </div>
  );
}
