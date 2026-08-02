import { getTranslations } from "next-intl/server";

import { resolveLocale } from "@/i18n/locale";
import { routing } from "@/i18n/routing";
import { buildManifest } from "@/lib/pwa/manifest";

/**
 * Serves `/{locale}/manifest.webmanifest` (OFF-10).
 *
 * Next's built-in `app/manifest.ts` convention only produces a single manifest at the origin root,
 * which cannot carry a localized name or a locale-prefixed `start_url` — so this is a route
 * handler under `[locale]` instead. `proxy.ts` already leaves paths containing a `.` alone, so the
 * locale negotiator never redirects it.
 */

/** Pre-render one manifest per locale at build time, matching the locale layout. */
export function generateStaticParams() {
  return routing.locales.map((locale) => ({ locale }));
}

export const dynamic = "force-static";

export async function GET(
  _request: Request,
  { params }: { params: Promise<{ locale: string }> },
): Promise<Response> {
  const locale = resolveLocale((await params).locale);
  const t = await getTranslations({ locale, namespace: "App" });

  const manifest = buildManifest(locale, {
    name: t("name"),
    shortName: t("shortName"),
    description: t("description"),
  });

  return new Response(JSON.stringify(manifest, null, 2), {
    headers: {
      // The spec-mandated type. Browsers are lenient about `application/json`, but installability
      // checks in some tooling are not.
      "Content-Type": "application/manifest+json",
    },
  });
}
