/**
 * The IANA zones to offer, including whatever is already stored.
 *
 * **The browser's list is not the set the API accepts, and assuming it was lost data.** An outlet
 * created with `UTC` — which `TimeZoneInfo.TryFindSystemTimeZoneById` accepts, so the API stored it
 * happily — rendered as an *empty* required select, because `Intl.supportedValuesOf("timeZone")`
 * returns canonical region zones and does not include it. Saving from that state forced whoever
 * opened the form to choose a different zone, silently changing the outlet's business day and the
 * validity window of every promotion on it.
 *
 * Legacy aliases behave the same way: a record holding one the runtime still resolves can be absent
 * from the enumerated list.
 *
 * So the stored value is always an option. It is put first because it is the answer already given,
 * and someone looking for it should not have to scroll past four hundred others to confirm it
 * survived.
 *
 * The rest still comes from the platform rather than a bundled list — a hard-coded set goes stale
 * every time a country changes its rules, and the API validates against the runtime's own database.
 */
export function zonesIncluding(stored: string | null | undefined): string[] {
  const known = typeof Intl.supportedValuesOf === "function" ? Intl.supportedValuesOf("timeZone") : [];

  return !stored || known.includes(stored) ? known : [stored, ...known];
}
