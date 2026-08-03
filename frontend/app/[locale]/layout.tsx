import type { Metadata, Viewport } from "next";
import { Geist } from "next/font/google";
import { NextIntlClientProvider } from "next-intl";
import { getTranslations, setRequestLocale } from "next-intl/server";

import { AuthProvider } from "@/components/auth-provider";
import { ServiceWorkerRegistrar } from "@/components/service-worker-registrar";
import { resolveLocale } from "@/i18n/locale";
import { routing } from "@/i18n/routing";
import { BRAND } from "@/lib/pwa/manifest";
import { cn } from "@/lib/utils";
import "../globals.css";

const geist = Geist({ subsets: ["latin"], variable: "--font-sans" });

type LocaleLayoutProps = Readonly<{
  children: React.ReactNode;
  params: Promise<{ locale: string }>;
}>;

/** Pre-render every locale at build time instead of on first request. */
export function generateStaticParams() {
  return routing.locales.map((locale) => ({ locale }));
}

/**
 * `theme_color` in the manifest is the brand teal — it tints the splash screen and the task
 * switcher. The `<meta name="theme-color">` values here are a different job: they colour the
 * browser/OS chrome *around the rendered page*, so they track `--background` per colour scheme and
 * the chrome blends into the page instead of banding against it.
 */
export const viewport: Viewport = {
  themeColor: [
    { media: "(prefers-color-scheme: light)", color: BRAND.backgroundLight },
    { media: "(prefers-color-scheme: dark)", color: BRAND.backgroundDark },
  ],
  // Reps use this on phones with notches and rounded corners; let the app paint edge to edge.
  viewportFit: "cover",
};

export async function generateMetadata({
  params,
}: Omit<LocaleLayoutProps, "children">): Promise<Metadata> {
  const locale = resolveLocale((await params).locale);
  const t = await getTranslations({ locale, namespace: "App" });

  return {
    title: t("name"),
    description: t("description"),
    // One manifest per locale (OFF-10) — see `lib/pwa/manifest.ts` for why it isn't shared.
    manifest: `/${locale}/manifest.webmanifest`,
    // iOS ignores the manifest for these; they only come from meta tags.
    appleWebApp: { capable: true, title: t("shortName"), statusBarStyle: "default" },
    icons: { apple: "/icons/apple-touch-icon.png" },
  };
}

export default async function LocaleLayout({ children, params }: LocaleLayoutProps) {
  const locale = resolveLocale((await params).locale);

  // Opts this subtree into static rendering (see `generateStaticParams`).
  setRequestLocale(locale);

  return (
    <html lang={locale} className={cn("font-sans", geist.variable)}>
      <body>
        <NextIntlClientProvider>
          {/*
            Deliberately not given Keycloak's address. This layout is statically rendered, so
            anything read from the environment here is baked in at build time — and a stale
            Keycloak port fails in the worst way, minting tokens whose issuer the API refuses.
            The address arrives from the two dynamic pages that need a live one; the provider
            restores an existing session from the device, which also makes it work offline.
          */}
          <AuthProvider locale={locale}>{children}</AuthProvider>
        </NextIntlClientProvider>
        <ServiceWorkerRegistrar />
      </body>
    </html>
  );
}
