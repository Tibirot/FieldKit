import { getTranslations, setRequestLocale } from "next-intl/server";

import { Breadcrumb } from "@/components/back-office/breadcrumb";
import { ChannelBrowser } from "@/components/back-office/channel-browser";
import { resolveLocale } from "@/i18n/locale";

/**
 * Trade classifications (`OUT-01`).
 *
 * Every outlet has a channel (`BR-OUT-1`) and it drives assortment, pricing and the visit workflow —
 * so a workspace with none cannot have outlets either. Its own route because it is set up once and
 * rarely revisited, unlike the outlet list beside it.
 */
export default async function ChannelsPage({ params }: { params: Promise<{ locale: string }> }) {
  const locale = resolveLocale((await params).locale);
  setRequestLocale(locale);

  const t = await getTranslations({ locale, namespace: "Channels" });

  return (
    <div className="flex max-w-2xl flex-col gap-4">
      <header>
        <Breadcrumb />
        <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
      </header>
      <ChannelBrowser />
    </div>
  );
}
