/**
 * Which calendar day an instant falls on, somewhere (`BR-PRD-6`) — W11½ R6b.
 *
 * The device's mirror of `SharedKernel/BusinessDay.cs`, pinned to it by
 * `vectors/pricing/business-day.v1.json`.
 *
 * **A business day is a date with no instant in it, and it starts at a different moment in every
 * place.** A price list, a promotion window and a tax rate all run by calendar day, so "which day"
 * has to be answered before any of them can be — and until this existed the two sides of the system
 * answered it differently. The device read `getFullYear`/`getMonth`/`getDate`, which is the *rep's
 * phone's* day; the server took the UTC date. Those are two rules, not one rule rounded twice, and
 * a rep in Bucharest before 03:00 was reported as disagreeing with a server that had asked a
 * different question (regression F6).
 *
 * **It takes a zone rather than finding one.** Whose day it is — the shop's, the rep's — is a
 * question this module deliberately has no opinion about. Pricing asks for the shop's;
 * [the round](../visits/today.ts) asks for the rep's and does not use this, because a journey plan
 * is a fact about the days somebody works (`JRN-03`).
 */

/**
 * The date `at` falls on in `timeZoneId` as `yyyy-mm-dd`, or `null` when this runtime cannot resolve
 * that zone.
 *
 * **Null rather than a fallback to UTC.** An unrecognised zone means the database that accepted the
 * value and the one reading it disagree — V8 and .NET do not ship identical zone data, and neither
 * does one phone versus another. Answering UTC would silently reinstate the defect this function
 * exists to remove, for exactly the shops nobody had noticed, and it would look like the rule
 * working. The caller declines instead.
 *
 * **Formatted in parts, never parsed from a localised string.** `toLocaleDateString` with a
 * `timeZone` returns whatever the locale spells — `17/03/2026`, `3/17/2026`, or a Buddhist-calendar
 * year — and a rule that read a date back out of that would be one locale change from being wrong.
 * `formatToParts` with the `en-CA` locale and an explicit Gregorian calendar asks for the fields
 * themselves.
 */
export function businessDay(at: Date, timeZoneId: string): string | null {
  /*
   * Rejected before `Intl` sees it — and `!timeZoneId` before `.trim()`, which is the load-bearing
   * half.
   *
   * <b>Measured rather than assumed.</b> `timeZone: ""` *throws* `RangeError` on the runtimes tested,
   * so the empty case would be caught below anyway. `timeZone: undefined` does not: it silently
   * formats in the **host's** zone, which is the original defect restored and invisible.
   *
   * The type says `string`, so `undefined` should be impossible. It is exactly one migration away:
   * `ReferenceOutlet.timeZoneId` was added in W11½ R6a, and a row that reached this before the
   * version 20 upgrade back-filled it has the property absent. A guard against a value the type
   * forbids is worth it when the store can produce it and the failure is silent.
   */
  if (!timeZoneId || !timeZoneId.trim()) return null;

  let parts: Intl.DateTimeFormatPart[];

  try {
    parts = new Intl.DateTimeFormat("en-CA", {
      timeZone: timeZoneId,
      calendar: "gregory",
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
    }).formatToParts(at);
  } catch {
    // `RangeError` for a zone this runtime does not know — the counterpart of .NET's
    // `TimeZoneNotFoundException`, and the case the shared vector file pins to one answer.
    return null;
  }

  const field = (type: Intl.DateTimeFormatPartTypes) =>
    parts.find((part) => part.type === type)?.value;

  const year = field("year");
  const month = field("month");
  const day = field("day");

  // Belt and braces on a formatter that answered without the fields asked for. Nothing observed
  // does this; the alternative is a string like "undefined-undefined-undefined" reaching a price
  // resolver, which would be read as a date the tenant simply has no list for.
  if (!year || !month || !day) return null;

  return `${year}-${month}-${day}`;
}
