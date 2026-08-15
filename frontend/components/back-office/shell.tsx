"use client";

import { useTranslations } from "next-intl";

import { useAuth } from "@/components/auth-provider";
import { QueryProvider } from "@/components/back-office/query-provider";
import { SectionPanel } from "@/components/back-office/section-panel";
import { Sidebar } from "@/components/back-office/sidebar";
import { SessionGuard } from "@/components/session-guard";
import { Button } from "@/components/ui/button";

/**
 * The back office (desktop console).
 *
 * The session states — restoring, anonymous, expired — live in
 * {@link SessionGuard}, which the field app shares. What is left here is the
 * console itself: a sidebar, a sign-out, and the query client the screens read through.
 */
export function BackOfficeShell({ children }: { children: React.ReactNode }) {
  return (
    <SessionGuard>
      <BackOffice>{children}</BackOffice>
    </SessionGuard>
  );
}

/** Rendered only once there is a session, so it can read one without checking. */
function BackOffice({ children }: { children: React.ReactNode }) {
  const t = useTranslations("BackOffice");
  const { workspace, signOut } = useAuth();

  return (
    <QueryProvider>
      <div className="flex min-h-dvh flex-col md:flex-row">
        <Sidebar workspace={workspace} />
        {/*
          Beside the sidebar rather than replacing anything (W12½ slice 3). Two columns of chrome is
          more than the finished shape asks for — the sidebar becomes a 68px rail in slice 4, taking
          the pair to 254px — and this is the interim state that keeps every screen reachable at
          every commit rather than only at the end of the stack.
        */}
        <SectionPanel />
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
