"use client";

import { useFormatter } from "next-intl";

/**
 * Renders a business date — `YYYY-MM-DD`, no time — as the same calendar day everywhere.
 *
 * **Both halves have to agree or the day moves.** `new Date("2026-01-01T00:00:00")` is parsed as
 * *local* midnight, and the app formats in UTC ([i18n request config](../i18n/request.ts), per
 * ADR-0010) — so east of Greenwich that renders as 31 December. Found on screen: an assignment
 * entered as starting on 1 January displayed as starting the day before.
 *
 * Pinning both ends to UTC is what makes it a calendar day rather than an instant, and it stays
 * right when the app-wide default becomes the user's own timezone: parse in UTC, format in UTC, and
 * nothing in between can shift it. A `DateOnly` has no moment to convert.
 *
 * A hook rather than two exports that must be used together, because "parse it this way and format
 * it that way" is exactly the pairing that comes apart at the next call site.
 */
export function useBusinessDay(): (iso: string) => string {
  const format = useFormatter();

  return (iso: string) =>
    format.dateTime(new Date(`${iso}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" });
}
