import { getTranslations, setRequestLocale } from "next-intl/server";

import { RetryButton } from "@/components/retry-button";
import { Card, CardDescription, CardFooter, CardHeader, CardTitle } from "@/components/ui/card";
import { resolveLocale } from "@/i18n/locale";

/**
 * The app-shell fallback the service worker serves when a navigation cannot reach the server
 * (OFF-10). It is precached at build time — one per locale — so it renders on a cold boot with no
 * connectivity at all.
 *
 * Deliberately static: no data, no client-side fetch. Anything that needs the network here would
 * defeat the purpose. Once the field modules land (Phase 2) most routes stop needing this page at
 * all, because they read the on-device store instead of the server (ADR-0004).
 */
export default async function Offline({ params }: { params: Promise<{ locale: string }> }) {
  setRequestLocale(resolveLocale((await params).locale));

  const t = await getTranslations("Offline");

  return (
    <main className="grid min-h-dvh place-items-center bg-background p-6">
      <Card className="w-full max-w-md text-center">
        <CardHeader className="space-y-2">
          <CardTitle className="text-2xl">{t("title")}</CardTitle>
          <CardDescription>{t("description")}</CardDescription>
        </CardHeader>
        <CardFooter className="flex-col gap-3">
          <RetryButton label={t("retry")} />
          <p className="text-xs text-muted-foreground">{t("hint")}</p>
        </CardFooter>
      </Card>
    </main>
  );
}
