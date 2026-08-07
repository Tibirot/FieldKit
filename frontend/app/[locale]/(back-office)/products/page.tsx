import { getTranslations, setRequestLocale } from "next-intl/server";

import { ProductBrowser } from "@/components/back-office/product-browser";
import { resolveLocale } from "@/i18n/locale";

/**
 * The product catalogue (`PRD-01`).
 *
 * The first Products screen, and the one every later one hangs off: an assortment says which of
 * these belong in which outlets, a price list says what they cost, a promotion discounts them. All
 * of that is authored against products that have to exist first.
 */
export default async function ProductsPage({ params }: { params: Promise<{ locale: string }> }) {
  const locale = resolveLocale((await params).locale);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "Products" });

  return (
    <div className="flex max-w-4xl flex-col gap-4">
      <header>
        <p className="font-mono text-[11.5px] text-muted-foreground">{t("crumb")}</p>
        <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
      </header>
      <ProductBrowser />
    </div>
  );
}
