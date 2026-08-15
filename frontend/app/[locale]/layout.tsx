import type { Metadata, Viewport } from "next";
import { Geist } from "next/font/google";
import { headers } from "next/headers";
import { NextIntlClientProvider } from "next-intl";
import { getTranslations, setRequestLocale } from "next-intl/server";

import { AuthProvider } from "@/components/auth-provider";
import { ServiceWorkerRegistrar } from "@/components/service-worker-registrar";
import { resolveLocale } from "@/i18n/locale";
import { routing } from "@/i18n/routing";
import { BRAND } from "@/lib/pwa/manifest";
import { THEME_BOOTSTRAP } from "@/lib/theme/theme";
import { cn } from "@/lib/utils";
import "../globals.css";

const geist = Geist({ subsets: ["latin"], variable: "--font-sans" });

type LocaleLayoutProps = Readonly<{
  children: React.ReactNode;
  params: Promise<{ locale: string }>;
}>;

/** Which locales exist. Still enumerated so every locale's metadata and routes are known. */
export function generateStaticParams() {
  return routing.locales.map((locale) => ({ locale }));
}

/**
 * Rendered per request, which is the price of the Content-Security-Policy.
 *
 * The policy nonces Next's own inline bootstrap, and a nonce has to be fresh per response or it is
 * not a nonce. A prerendered document carries whatever nonce existed at build time — so with static
 * rendering the header and the HTML disagree on every request, every script is refused, and the app
 * renders a blank page. That is not a theory: it is what the first version of this change did, and
 * the console said so 25 times.
 *
 * The cost is small *here* specifically. These pages are shells — every one of them fetches its data
 * from the API in the browser after hydrating, so prerendering was saving a render of markup that
 * contains no data, not a round trip. Offline still works: the service worker caches the document
 * together with its response headers, so a cached page keeps the nonce it was served with.
 */
export const dynamic = "force-dynamic";

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

  /*
   * The nonce the proxy minted for this response, which the pre-paint script below cannot run
   * without: `script-src` is `'self' 'nonce-…' 'strict-dynamic'`, so an un-nonced inline script is
   * refused — and refused *silently*, leaving the theme to fall back to the device preference with
   * nothing in the UI to say why. Read from the request header rather than regenerated, because a
   * second nonce would match neither the policy nor Next's own bootstrap.
   */
  const nonce = (await headers()).get("x-nonce") ?? undefined;

  return (
    /*
     * `suppressHydrationWarning` because the script below writes to this element's class list before
     * React sees the document, so the server's markup and the client's DOM disagree here **by
     * design**. Scoped to `<html>` itself: it suppresses the warning for this element's attributes
     * and not for its subtree, so a real mismatch inside the app still reports.
     */
    <html
      lang={locale}
      suppressHydrationWarning
      className={cn("font-sans", geist.variable)}
    >
      <head>
        {/*
          Before the first paint, before the body exists, and before anything is fetched.

          A module cannot do this job: it would be parsed and run *after* the document painted, so
          the page would appear in whatever the CSS resolved to and then change colour under the
          reader — worse than the wrong theme arriving in the first place. That is the property this
          slice trades away by making the theme a choice at all, and this is what buys it back.

          It survives the service worker because the worker caches the document *with its response
          headers* (see `dynamic` above), so a page served from cache keeps both the script and the
          nonce that authorises it.
        */}
        {/*
          `suppressHydrationWarning` here too, and for a different reason from the one on `<html>`:
          React deliberately does not carry `nonce` into the client tree — reading a live nonce out
          of the DOM is exactly what a nonce is meant to prevent — so the attribute is present on the
          server and empty on the client, every time, by design.
        */}
        <script
          nonce={nonce}
          suppressHydrationWarning
          dangerouslySetInnerHTML={{ __html: THEME_BOOTSTRAP }}
        />
      </head>
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
