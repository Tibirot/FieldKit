import { setRequestLocale } from "next-intl/server";

import { TaxRates } from "@/components/back-office/tax-rates";
import { resolveLocale } from "@/i18n/locale";

/**
 * What a tax class is taxed at (`PRD-07`).
 *
 * Its own route rather than a section of the classification screen, because a class and its rates
 * are different lifetimes: the class is named once and the rates change every time a government
 * changes them, in one country at a time.
 */
export default async function TaxRatesPage({ params }: { params: Promise<{ locale: string }> }) {
  setRequestLocale(resolveLocale((await params).locale));

  return <TaxRates />;
}
