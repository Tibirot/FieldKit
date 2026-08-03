import { getFormatter, getTranslations, setRequestLocale } from "next-intl/server";

import { IdentityCard } from "@/components/identity-card";
import { LocaleSwitcher } from "@/components/locale-switcher";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardFooter,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { resolveLocale } from "@/i18n/locale";
import { routing } from "@/i18n/routing";

/**
 * Stand-in figures until the real modules land — enough to prove that money, quantities and
 * timestamps are formatted by the locale rather than hard-coded. A fixed instant (not `new Date()`)
 * keeps the page deterministic: no hydration mismatch, no snapshot churn.
 */
const SAMPLE = {
  price: 1249.5,
  currency: "USD",
  cases: 1830,
  syncedAt: new Date("2026-08-02T09:15:00Z"),
};

export default async function Home({
  params,
}: {
  params: Promise<{ locale: string }>;
}) {
  setRequestLocale(resolveLocale((await params).locale));

  const t = await getTranslations("Home");
  const tl = await getTranslations("Localization");
  const format = await getFormatter();

  return (
    <main className="grid min-h-dvh place-items-center bg-background p-6">
      <div className="flex w-full max-w-lg flex-col items-center gap-4">
        <LocaleSwitcher />

        <Card className="w-full text-center">
          <CardHeader className="space-y-2">
            <Badge
              variant="secondary"
              className="mx-auto font-mono text-[0.65rem] tracking-widest uppercase"
            >
              {t("eyebrow")}
            </Badge>
            <CardTitle className="text-4xl tracking-tight">{t("title")}</CardTitle>
            <CardDescription className="text-base">{t("description")}</CardDescription>
          </CardHeader>
          <CardFooter className="justify-center gap-3">
            <Button>{t("primaryCta")}</Button>
            <Button variant="outline">{t("secondaryCta")}</Button>
          </CardFooter>
        </Card>

        <IdentityCard />

        <Card className="w-full" size="sm">
          <CardHeader>
            <CardTitle>{tl("title")}</CardTitle>
            <CardDescription>
              {tl("description", { count: routing.locales.length })}
            </CardDescription>
          </CardHeader>
          <CardContent>
            <dl className="grid gap-2 text-sm">
              <div className="flex justify-between gap-4">
                <dt className="text-muted-foreground">{tl("priceLabel")}</dt>
                <dd className="font-medium tabular-nums">
                  {format.number(SAMPLE.price, {
                    style: "currency",
                    currency: SAMPLE.currency,
                  })}
                </dd>
              </div>
              <div className="flex justify-between gap-4">
                <dt className="text-muted-foreground">{tl("quantityLabel")}</dt>
                <dd className="font-medium tabular-nums">{format.number(SAMPLE.cases)}</dd>
              </div>
              <div className="flex justify-between gap-4">
                <dt className="text-muted-foreground">{tl("syncedLabel")}</dt>
                <dd className="font-medium">
                  {format.dateTime(SAMPLE.syncedAt, {
                    dateStyle: "long",
                    timeStyle: "short",
                  })}
                </dd>
              </div>
            </dl>
          </CardContent>
          <CardFooter>
            <p className="text-xs text-muted-foreground">{tl("timeZoneNote")}</p>
          </CardFooter>
        </Card>
      </div>
    </main>
  );
}
