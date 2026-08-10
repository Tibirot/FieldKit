import { setRequestLocale } from "next-intl/server";

import { FieldShell } from "@/components/field/shell";
import { resolveLocale } from "@/i18n/locale";

/**
 * The field app (the phone in the shop), as a route group.
 *
 * A group rather than a path segment, like `(back-office)` beside it: `(field)` shapes the layout
 * without appearing in the URL. Two experiences, one app (ADR-0004) — this one is mobile-first and
 * reads the local store; that one is a desktop console and reads the API.
 *
 * Statically rendered, and that is the load-bearing part here rather than a performance note.
 * Everything that varies per rep — the session, the device, the data — is client-side below
 * `FieldShell`, which is what lets a cold start with no signal paint an app instead of waiting on a
 * server that is not there.
 */
export default async function FieldLayout({
  children,
  params,
}: {
  children: React.ReactNode;
  params: Promise<{ locale: string }>;
}) {
  setRequestLocale(resolveLocale((await params).locale));

  return <FieldShell>{children}</FieldShell>;
}
