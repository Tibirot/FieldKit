"use client";

import { useTranslations } from "next-intl";
import { useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardFooter,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import type { OidcSettings } from "@/lib/auth/oidc";
import { isValidWorkspace, normalizeWorkspace } from "@/lib/auth/workspace";

/**
 * Asks which tenant, then hands off to that tenant's Keycloak realm (`IAM-01`, ADR-0008).
 *
 * FieldKit never sees the password — this form collects a workspace and nothing else. The
 * credential is entered on Keycloak's own page, which is the point of using an identity provider.
 */
export function LoginForm({
  initialWorkspace,
  settings,
}: {
  initialWorkspace: string;
  /** `null` when the deployment has no identity provider configured. */
  settings: OidcSettings | null;
}) {
  const t = useTranslations("Login");
  const { signIn } = useAuth();

  const [workspace, setWorkspace] = useState(initialWorkspace);
  const [submitted, setSubmitted] = useState(false);
  const [failed, setFailed] = useState(false);

  const normalized = normalizeWorkspace(workspace);
  const invalid = submitted && !isValidWorkspace(normalized);

  async function onSubmit(event: React.FormEvent) {
    event.preventDefault();
    setSubmitted(true);
    setFailed(false);

    if (!isValidWorkspace(normalized) || !settings) return;

    try {
      await signIn(normalized, settings);
    } catch {
      // The redirect never happened — an unreachable Keycloak, or a realm that does not exist.
      // Either way the user is still here and needs to be told, not left on a dead button.
      setFailed(true);
    }
  }

  const disabled = !settings;

  return (
    <Card className="w-full">
      <form onSubmit={onSubmit} noValidate>
        <CardHeader>
          <CardTitle>{t("title")}</CardTitle>
          <CardDescription>{t("description")}</CardDescription>
        </CardHeader>

        <CardContent className="space-y-2">
          <label className="block text-sm font-medium" htmlFor="workspace">
            {t("workspaceLabel")}
          </label>
          <input
            id="workspace"
            name="workspace"
            value={workspace}
            onChange={(event) => setWorkspace(event.target.value)}
            autoComplete="organization"
            autoCapitalize="none"
            autoCorrect="off"
            spellCheck={false}
            disabled={disabled}
            aria-invalid={invalid}
            aria-describedby="workspace-hint"
            className="w-full rounded-md border border-input bg-transparent px-3 py-2 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 aria-invalid:border-destructive disabled:opacity-50"
          />
          <p
            id="workspace-hint"
            className={invalid ? "text-xs text-destructive" : "text-xs text-muted-foreground"}
          >
            {invalid ? t("workspaceInvalid") : t("workspaceHint")}
          </p>

          {disabled ? <p className="text-sm text-destructive">{t("unconfigured")}</p> : null}
          {failed ? <p className="text-sm text-destructive">{t("redirectFailed")}</p> : null}
        </CardContent>

        <CardFooter>
          <Button type="submit" disabled={disabled}>
            {t("submit")}
          </Button>
        </CardFooter>
      </form>
    </Card>
  );
}
