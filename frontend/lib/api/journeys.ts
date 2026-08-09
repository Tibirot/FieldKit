import { apiDelete, apiGet, apiSend } from "@/lib/api/client";

/**
 * How often a shop is called on: a number of visits over a number of days (`JRN-01`).
 *
 * **Two numbers, not a word.** "Weekly" and "2× a month" are the same kind of statement, and the
 * pair expresses both without a vocabulary anybody has to agree on first. It is also what generation
 * consumes, so nothing translates between what an admin sets and what the planner reads.
 */
export type Frequency = {
  visitsPerCycle: number;
  cycleLengthDays: number;
};

/** A segment's default — the rule that covers every shop nobody said anything else about. */
export type SegmentFrequency = Frequency & { segment: string };

/** One shop's override of its segment's default. */
export type OutletFrequency = Frequency & { outletId: string };

/**
 * What an outlet is actually due, and which rule decided it.
 *
 * `source` is the point: "why is this shop planned four times a month?" is the question an admin
 * asks, and a number alone cannot answer it.
 */
export type ResolvedFrequency = Frequency & {
  outletId: string;
  source: "Outlet" | "Segment";
};

const FREQUENCIES = "/api/journey/frequencies";

export function fetchSegmentFrequencies(
  accessToken: string,
  signal?: AbortSignal,
): Promise<SegmentFrequency[]> {
  return apiGet<SegmentFrequency[]>(`${FREQUENCIES}/segments`, accessToken, signal);
}

/**
 * Sets a segment's default.
 *
 * A PUT keyed by the segment label, because a segment has at most one frequency — saving twice has
 * saved once, which is what makes this safe to retry and what stops a double-click becoming a 409
 * about a row the caller never asked to create.
 */
export function setSegmentFrequency(
  accessToken: string,
  segment: string,
  frequency: Frequency,
): Promise<SegmentFrequency> {
  return apiSend<SegmentFrequency>(
    "PUT",
    `${FREQUENCIES}/segments/${encodeURIComponent(segment)}`,
    accessToken,
    frequency,
  );
}

export function deleteSegmentFrequency(accessToken: string, segment: string): Promise<void> {
  return apiDelete(`${FREQUENCIES}/segments/${encodeURIComponent(segment)}`, accessToken);
}

export function fetchOutletFrequencies(
  accessToken: string,
  signal?: AbortSignal,
): Promise<OutletFrequency[]> {
  return apiGet<OutletFrequency[]>(`${FREQUENCIES}/outlets`, accessToken, signal);
}

export function setOutletFrequency(
  accessToken: string,
  outletId: string,
  frequency: Frequency,
): Promise<OutletFrequency> {
  return apiSend<OutletFrequency>(
    "PUT",
    `${FREQUENCIES}/outlets/${outletId}`,
    accessToken,
    frequency,
  );
}

/**
 * Removes one shop's override, returning it to its segment's default.
 *
 * Not "set it back to the segment's numbers" — that would look identical today and stop tracking the
 * default the moment somebody changes it.
 */
export function deleteOutletFrequency(accessToken: string, outletId: string): Promise<void> {
  return apiDelete(`${FREQUENCIES}/outlets/${outletId}`, accessToken);
}

export const segmentFrequenciesKey = (subject: string) => ["frequencies", subject, "segments"] as const;

export const outletFrequenciesKey = (subject: string) => ["frequencies", subject, "outlets"] as const;

/**
 * Whether a pair of numbers is a frequency the server will accept.
 *
 * Checked here so a typo is a message beside the field rather than a refusal about the whole rule.
 * The server checks the same things and is the authority — this only decides whether it is worth
 * asking. The cycle ceiling is `CallFrequency.MaximumCycleLengthDays`: a cycle longer than a year
 * is a shop nobody is really calling on.
 */
export const MAXIMUM_CYCLE_DAYS = 365;

export function frequencyProblem(
  visits: string,
  cycleDays: string,
): "visits" | "cycle" | null {
  const parsedVisits = Number(visits);
  const parsedCycle = Number(cycleDays);

  if (!Number.isInteger(parsedVisits) || parsedVisits < 1) return "visits";
  if (!Number.isInteger(parsedCycle) || parsedCycle < 1 || parsedCycle > MAXIMUM_CYCLE_DAYS) {
    return "cycle";
  }

  return null;
}

// ── The working calendar (JRN-02) ──────────────────────────────────────────────────────────────

/**
 * A day of the week, by name.
 *
 * Names on the wire, never ordinals — the rule every enum on this API follows, and one that matters
 * more here than usual: .NET's `DayOfWeek` numbers the week from **Sunday**, so a calendar built
 * from numbers would be off by one in a way nobody reading the JSON would question.
 */
export type WeekdayName =
  | "Monday"
  | "Tuesday"
  | "Wednesday"
  | "Thursday"
  | "Friday"
  | "Saturday"
  | "Sunday";

/**
 * The week as this screen renders it — **Monday first**.
 *
 * A display order, and deliberately not the server's. The API takes names precisely so that the two
 * can differ: a Romanian week starts on Monday, and nothing about how the days are stored should
 * decide how they are read.
 */
export const WEEK: readonly WeekdayName[] = [
  "Monday",
  "Tuesday",
  "Wednesday",
  "Thursday",
  "Friday",
  "Saturday",
  "Sunday",
];

/** A rep's working pattern: which days they work, and how many calls a day holds. */
export type WorkingCalendar = {
  userId: string;
  displayName: string | null;
  workingDays: WeekdayName[];
  visitsPerDay: number;
};

/** A date nobody works. Tenant-wide — a holiday is a fact about the country, not about a rep. */
export type Holiday = {
  id: string;
  date: string;
  name: string;
};

const CALENDARS = "/api/journey/calendars";
const HOLIDAYS = "/api/journey/holidays";

export function fetchCalendars(
  accessToken: string,
  signal?: AbortSignal,
): Promise<WorkingCalendar[]> {
  return apiGet<WorkingCalendar[]>(CALENDARS, accessToken, signal);
}

/** Sets a rep's pattern. PUT keyed by the rep, because a rep has at most one calendar. */
export function setCalendar(
  accessToken: string,
  userId: string,
  workingDays: readonly WeekdayName[],
  visitsPerDay: number,
): Promise<WorkingCalendar> {
  return apiSend<WorkingCalendar>("PUT", `${CALENDARS}/${encodeURIComponent(userId)}`, accessToken, {
    workingDays,
    visitsPerDay,
  });
}

/**
 * Removes a rep's calendar, which makes them **unconfigured** rather than unavailable.
 *
 * There is no "works no days" to fall back to — the server refuses a calendar with an empty week for
 * exactly that reason. Generation plans nothing for a rep with no calendar and says so as a
 * shortfall, which is a different and more useful answer than a plan with no days in it.
 */
export function deleteCalendar(accessToken: string, userId: string): Promise<void> {
  return apiDelete(`${CALENDARS}/${encodeURIComponent(userId)}`, accessToken);
}

export function fetchHolidays(accessToken: string, signal?: AbortSignal): Promise<Holiday[]> {
  return apiGet<Holiday[]>(HOLIDAYS, accessToken, signal);
}

/**
 * Adds a holiday.
 *
 * POST rather than PUT, because a date is not a name the caller chooses: holidays are a list a
 * tenant adds to, and adding Christmas twice is a refusal rather than a silent overwrite.
 */
export function addHoliday(accessToken: string, date: string, name: string): Promise<Holiday> {
  return apiSend<Holiday>("POST", HOLIDAYS, accessToken, { date, name });
}

export function deleteHoliday(accessToken: string, id: string): Promise<void> {
  return apiDelete(`${HOLIDAYS}/${id}`, accessToken);
}

export const calendarsKey = (subject: string) => ["calendars", subject] as const;

export const holidaysKey = (subject: string) => ["holidays", subject] as const;

/** `WorkingCalendar.MaximumVisitsPerDay`, so a typo lands beside the field. */
export const MAXIMUM_VISITS_PER_DAY = 50;

export function capacityProblem(visitsPerDay: string): boolean {
  const parsed = Number(visitsPerDay);

  return !Number.isInteger(parsed) || parsed < 1 || parsed > MAXIMUM_VISITS_PER_DAY;
}
