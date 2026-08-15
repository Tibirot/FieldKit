import { getTranslations, setRequestLocale } from "next-intl/server";

import { Breadcrumb } from "@/components/back-office/breadcrumb";
import { CallFrequencies } from "@/components/back-office/call-frequencies";
import { resolveLocale } from "@/i18n/locale";

/**
 * How often each shop is called on (`JRN-01`).
 *
 * The first thing in the Journeys section, because everything else in it is derived from this: the
 * generator turns frequency × territory × working calendar into a plan, and the compliance figure
 * asks whether a shop got the visits its frequency said it should. It is also the only part a
 * supervisor sets by hand, which is why it exists before there is anything to generate.
 */
export default async function FrequenciesPage({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  const locale = resolveLocale((await params).locale);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "Frequencies" });

  return (
    <div className="flex max-w-4xl flex-col gap-4">
      <header className="flex flex-wrap items-start gap-3">
        <div className="min-w-0 flex-1">
          <Breadcrumb />
          <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
          <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
        </div>
      </header>
      <CallFrequencies />
    </div>
  );
}
