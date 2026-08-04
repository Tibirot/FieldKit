"use client";

import { useTranslations } from "next-intl";
import { useEffect } from "react";

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
  const { status, workspace, signOut } = useAuth();
  const router = useRouter();

  // In an effect rather than during render: redirecting while rendering is a side effect in the
  // render path, and React will run it twice in development and shout about it.
  useEffect(() => {
    if (status === "anonymous") {
      router.replace("/login");
    }
  }, [status, router]);

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
