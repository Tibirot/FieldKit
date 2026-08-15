import { setRequestLocale } from "next-intl/server";

import { LocaleSwitcher } from "@/components/locale-switcher";
import { LoginForm } from "@/components/login-form";
import { ThemeToggle } from "@/components/theme-toggle";
import { resolveLocale } from "@/i18n/locale";
import { readOidcSettings } from "@/lib/auth/settings";

/**
 * Sign-in (`IAM-01`).
 *
 * Rendered per request, unlike the rest of the shell. Keycloak's address comes from the environment
 * — Aspire assigns its port per run, and a containerised deploy is built long before it learns where
 * its identity provider lives. Prerendering this page would bake in one of those and mint tokens
 * whose issuer the API refuses, which presents as a login that works and an app that stays logged
 * out. Nothing is lost: signing in requires the network anyway.
 */
export const dynamic = "force-dynamic";

/**
 * Prefills the field with the realm the AppHost imports, so the demo path is one click rather than a
 * guess at a realm name. A placeholder, not a value — signing in to a real tenant overwrites it, and
 * the app remembers what was typed instead.
 */
const DEV_WORKSPACE = "fieldkit-dev";

export default async function LoginPage({ params }: { params: Promise<{ locale: string }> }) {
  setRequestLocale(resolveLocale((await params).locale));

  return (
    <main className="grid min-h-dvh place-items-center bg-background p-6">
      <div className="flex w-full max-w-md flex-col items-center gap-4">
        <div className="flex items-center gap-2">
          <LocaleSwitcher />
          <ThemeToggle />
        </div>
        <LoginForm initialWorkspace={DEV_WORKSPACE} settings={readOidcSettings()} />
      </div>
    </main>
  );
}
