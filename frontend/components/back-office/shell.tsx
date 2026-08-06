"use client";

import { useTranslations } from "next-intl";
import { useEffect, useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { QueryProvider } from "@/components/back-office/query-provider";
import { Sidebar } from "@/components/back-office/sidebar";
import { Button } from "@/components/ui/button";
import { useRouter } from "@/i18n/navigation";

/**
 * The back office, and the guard in front of it (`IAM-01`).
 *
 * The guard is client-side because the session is: tokens live on the device, and the layout above
 * this is statically rendered so a rep opening the app offline still gets a shell. A server-side
 * check would need the token on the server, which is the architecture this app deliberately does not
 * have.
 *
 * That makes this a *routing* guard, not a security boundary. Nothing here decides what anyone may
 * see — the API re-validates every request against the token it was given (ADR-0008), so the worst a
 * bypassed redirect achieves is an empty screen full of 401s.
 */
export function BackOfficeShell({ children }: { children: React.ReactNode }) {
  const t = useTranslations("BackOffice");
  const { status, workspace, signOut, reauthenticate } = useAuth();
  const router = useRouter();
  const [reauthenticating, setReauthenticating] = useState(false);

  // In an effect rather than during render: redirecting while rendering is a side effect in the
  // render path, and React will run it twice in development and shout about it.
  useEffect(() => {
    if (status === "anonymous") {
      router.replace("/login");
    }
  }, [status, router]);

  /**
   * An expired session is a question, not a verdict, so it gets asked rather than acted on.
   *
   * Redirecting to `/login` would work and would be less code. It also throws away the two things
   * the app already knows — which workspace, and that this person was signed in a moment ago — and
   * makes someone re-type a realm name to get back to the page they were on. Worse, an automatic
   * redirect mid-task looks identical to the app losing their work.
   */
  const signInAgain = async () => {
    setReauthenticating(true);

    // No `finally`: on success this navigates away from the page, and clearing the flag on an
    // unmounting component is both pointless and a warning. Only the failure path returns here.
    if (!(await reauthenticate())) {
      setReauthenticating(false);
      router.replace("/login");
    }
  };

  if (status === "expired") {
    return (
      <main className="grid min-h-dvh place-items-center p-6">
        <div className="flex max-w-sm flex-col items-center gap-4 text-center" role="alert">
          <div className="flex flex-col gap-1">
            <h1 className="text-lg font-medium">{t("expired.title")}</h1>
            <p className="text-sm text-muted-foreground">{t("expired.body")}</p>
          </div>
          <div className="flex flex-wrap items-center justify-center gap-2">
            <Button onClick={() => void signInAgain()} disabled={reauthenticating}>
              {reauthenticating ? t("expired.signingIn") : t("expired.signIn")}
            </Button>
            <Button variant="outline" onClick={() => void signOut()} disabled={reauthenticating}>
              {t("signOut")}
            </Button>
          </div>
        </div>
      </main>
    );
  }

  // "We have not looked yet" is not "there is nobody" — collapsing them flashes the sign-in screen
  // at an already signed-in user on every reload.
  if (status !== "authenticated") {
    return (
      <main className="grid min-h-dvh place-items-center p-6">
        <p className="text-sm text-muted-foreground" role="status">
          {status === "loading" ? t("restoring") : t("redirecting")}
        </p>
      </main>
    );
  }

  return (
    <QueryProvider>
      <div className="flex min-h-dvh flex-col md:flex-row">
        <Sidebar workspace={workspace} />
        <div className="flex min-w-0 flex-1 flex-col">
          <header className="flex items-center justify-end gap-3 border-b border-border px-6 py-3">
            <Button variant="outline" size="sm" onClick={() => void signOut()}>
              {t("signOut")}
            </Button>
          </header>
          <main className="min-w-0 flex-1 p-6">{children}</main>
        </div>
      </div>
    </QueryProvider>
  );
}
