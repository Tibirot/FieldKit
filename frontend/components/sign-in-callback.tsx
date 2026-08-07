"use client";

import { useTranslations } from "next-intl";
import { useEffect, useRef, useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { LANDING } from "@/components/back-office/navigation";
import { Card, CardDescription, CardFooter, CardHeader, CardTitle } from "@/components/ui/card";
import { LinkButton } from "@/components/ui/link-button";
import { useRouter } from "@/i18n/navigation";
import type { OidcSettings } from "@/lib/auth/oidc";

/**
 * Completes the redirect back from Keycloak (`IAM-01`).
 *
 * The URL arriving here carries a one-time authorization code. It is exchanged once and the user is
 * moved off this route immediately — `replace`, not `push`, so Back does not return to a URL whose
 * code has already been spent.
 */
export function SignInCallback({ settings }: { settings: OidcSettings | null }) {
  const t = useTranslations("Callback");
  const router = useRouter();
  const { completeSignIn } = useAuth();

  const [failed, setFailed] = useState(false);

  // React runs effects twice in development. The code exchange is not idempotent — the second
  // attempt fails against a spent code — so the guard is load-bearing, not a tidiness measure.
  const started = useRef(false);

  useEffect(() => {
    if (started.current) return;
    started.current = true;

    const completing = settings
      ? completeSignIn(settings)
      : Promise.reject(new Error("Keycloak is not configured."));

    // Straight into the back office rather than the app root: signing in is an intent to work, and a
    // landing page between the two is a page nobody reads. LANDING is Outlets because that is the
    // first screen that exists — see the UX build-scope note.
    completing.then(() => router.replace(LANDING)).catch(() => setFailed(true));
  }, [completeSignIn, router, settings]);

  return (
    <Card className="w-full">
      <CardHeader>
        <CardTitle>{failed ? t("failedTitle") : t("title")}</CardTitle>
        <CardDescription>{failed ? t("failedDescription") : t("description")}</CardDescription>
      </CardHeader>
      {failed ? (
        <CardFooter>
          <LinkButton href="/login">{t("retry")}</LinkButton>
        </CardFooter>
      ) : null}
    </Card>
  );
}
