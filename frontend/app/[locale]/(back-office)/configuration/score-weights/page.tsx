import { getTranslations, setRequestLocale } from "next-intl/server";

import { ScoreWeights } from "@/components/back-office/score-weights";
import { resolveLocale } from "@/i18n/locale";

/**
 * The tenant's perfect-store weighting (`AUD-07`, `BR-AUD-4`).
 *
 * The first screen in the Configuration section, and the first half of the wireframe's
 * "visit-workflow / audit builder" to be built. Weights before surveys because the score is what an
 * administrator is asked about first, and because the one-way publish is the decision the whole of
 * W10 was arranged around — a screen that made that legible was worth having before one that adds
 * questions to a form.
 */
export default async function ScoreWeightsPage({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  const locale = resolveLocale((await params).locale);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "ScoreWeights" });

  return (
    <div className="flex max-w-4xl flex-col gap-4">
      <header className="min-w-0">
        <p className="font-mono text-[11.5px] text-muted-foreground">{t("crumb")}</p>
        <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
      </header>
      <ScoreWeights />
    </div>
  );
}
