import { setRequestLocale } from "next-intl/server";

import { BackOfficeShell } from "@/components/back-office/shell";
import { resolveLocale } from "@/i18n/locale";

/**
 * The back office (desktop console), as a route group.
 *
 * A group rather than a path segment: `(back-office)` shapes the layout without appearing in the
 * URL, so the screens stay at `/outlets` and `/territories` the way the wireframes address them. The
 * field app will sit in its own group beside this one — two experiences, one app (ADR-0004).
 *
 * Statically rendered like the rest of the shell. Everything that varies per user — the session, the
 * data, the guard — is client-side below `BackOfficeShell`, which is also what lets a cold offline
 * start paint something rather than wait on a server that is not there.
 */
export default async function BackOfficeLayout({
  children,
  params,
}: {
  children: React.ReactNode;
  params: Promise<{ locale: string }>;
}) {
  setRequestLocale(resolveLocale((await params).locale));

  return <BackOfficeShell>{children}</BackOfficeShell>;
}
