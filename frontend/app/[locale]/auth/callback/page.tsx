import { setRequestLocale } from "next-intl/server";

import { SignInCallback } from "@/components/sign-in-callback";
import { resolveLocale } from "@/i18n/locale";
import { readOidcSettings } from "@/lib/auth/settings";

/**
 * Where Keycloak sends the browser back after sign-in (`IAM-01`).
 *
 * Dynamic for the same reason as the sign-in page: the token exchange has to go to the address the
 * code was actually issued by, and that address is not known at build time.
 */
export const dynamic = "force-dynamic";

export default async function CallbackPage({ params }: { params: Promise<{ locale: string }> }) {
  setRequestLocale(resolveLocale((await params).locale));

  return (
    <main className="grid min-h-dvh place-items-center bg-background p-6">
      <div className="w-full max-w-md">
        <SignInCallback settings={readOidcSettings()} />
      </div>
    </main>
  );
}
