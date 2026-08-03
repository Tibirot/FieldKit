"use client";

import { useTranslations } from "next-intl";
import { useEffect, useState } from "react";

import { useAuth } from "@/components/auth-provider";
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
import { Link } from "@/i18n/navigation";
import { fetchIdentity, type Identity } from "@/lib/api/identity";

/**
 * The session, as the API sees it (`IAM-01`).
 *
 * This is the proof the slice exists to provide: a token minted by a tenant's realm, validated by
 * the API, coming back as a tenant and a set of permissions. Rendering claims decoded in the
 * browser would look identical and prove nothing.
 */
export function IdentityCard() {
  const t = useTranslations("Session");
  const { status, user, workspace, signOut } = useAuth();

  const [identity, setIdentity] = useState<Identity | null>(null);
  const [failed, setFailed] = useState(false);

  const accessToken = user?.access_token;

  // No clearing when the token goes away: signing out flips `status` to anonymous, and that branch
  // returns before any of this is rendered. Resetting here would be a cascading render to hide
  // something already unreachable.
  useEffect(() => {
    if (!accessToken) return;

    const controller = new AbortController();

    fetchIdentity(accessToken, controller.signal)
      .then(setIdentity)
      .catch((error: unknown) => {
        // An aborted request is this effect being cleaned up, not a failure to report.
        if (!controller.signal.aborted) {
          setFailed(true);
        }

        void error;
      });

    return () => controller.abort();
  }, [accessToken]);

  if (status === "loading") {
    return (
      <Card className="w-full" size="sm">
        <CardHeader>
          <CardTitle>{t("title")}</CardTitle>
          <CardDescription>{t("loading")}</CardDescription>
        </CardHeader>
      </Card>
    );
  }

  if (status === "anonymous") {
    return (
      <Card className="w-full" size="sm">
        <CardHeader>
          <CardTitle>{t("title")}</CardTitle>
          <CardDescription>{t("anonymous")}</CardDescription>
        </CardHeader>
        <CardFooter>
          {/* Renders an <a>, so Base UI's native-button assumption has to be switched off. */}
          <Button nativeButton={false} render={<Link href="/login" />}>
            {t("signIn")}
          </Button>
        </CardFooter>
      </Card>
    );
  }

  return (
    <Card className="w-full" size="sm">
      <CardHeader>
        <CardTitle>{t("title")}</CardTitle>
        <CardDescription>{t("signedInAs", { workspace: workspace ?? "" })}</CardDescription>
      </CardHeader>
      <CardContent>
        {failed ? (
          <p className="text-sm text-destructive">{t("apiUnreachable")}</p>
        ) : !identity ? (
          <p className="text-sm text-muted-foreground">{t("loading")}</p>
        ) : (
          <dl className="grid gap-2 text-sm">
            <div className="flex justify-between gap-4">
              <dt className="text-muted-foreground">{t("subjectLabel")}</dt>
              <dd className="truncate font-mono text-xs">{identity.subject}</dd>
            </div>
            <div className="flex justify-between gap-4">
              <dt className="text-muted-foreground">{t("tenantLabel")}</dt>
              <dd className="truncate font-mono text-xs">{identity.tenant}</dd>
            </div>
            <div className="flex flex-col gap-2">
              <dt className="text-muted-foreground">{t("permissionsLabel")}</dt>
              <dd className="flex flex-wrap gap-1.5">
                {identity.permissions.map((permission) => (
                  <Badge key={permission} variant="secondary" className="font-mono text-[0.65rem]">
                    {permission}
                  </Badge>
                ))}
              </dd>
            </div>
          </dl>
        )}
      </CardContent>
      <CardFooter>
        <Button variant="outline" onClick={() => void signOut()}>
          {t("signOut")}
        </Button>
      </CardFooter>
    </Card>
  );
}
