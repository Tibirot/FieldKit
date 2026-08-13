"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useTranslations } from "next-intl";
import { useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import {
  addHoliday,
  calendarsKey,
  capacityProblem,
  deleteCalendar,
  deleteHoliday,
  fetchCalendars,
  fetchHolidays,
  holidaysKey,
  setCalendar,
  WEEK,
  type Holiday,
  type WeekdayName,
  type WorkingCalendar,
} from "@/lib/api/journeys";
import { refusalTexts } from "@/lib/api/refusals";
import { fetchUsers, identifying, usersKey, type User } from "@/lib/api/users";
import { usePermissions } from "@/lib/auth/use-permissions";
import { useBusinessDay } from "@/lib/dates";

const CONTROL =
  "h-8 rounded-lg border border-input bg-background px-2 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/**
 * When a rep works, and what the tenant does not (`JRN-02`).
 *
 * **A calendar is not availability.** It says which days of the week a rep is out and how many calls
 * a day holds — the two numbers generation multiplies into capacity. It says nothing about this
 * Tuesday in particular; a shut Tuesday is a holiday, which is tenant-wide because a national day
 * off is a fact about the country rather than about a person.
 *
 * **No calendar means unconfigured, not unavailable.** There is no "works no days" — the server
 * refuses an empty week for that reason — so a rep nobody has set up is planned for not at all, and
 * generation reports it rather than producing an empty round that looks like one.
 */
export function WorkingCalendars() {
  const t = useTranslations("Calendars");
  const { user } = useAuth();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;
  const enabled = Boolean(accessToken && subject);

  const calendars = useQuery({
    enabled,
    queryKey: calendarsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchCalendars(accessToken!, signal),
  });

  const holidays = useQuery({
    enabled,
    queryKey: holidaysKey(subject ?? ""),
    queryFn: ({ signal }) => fetchHolidays(accessToken!, signal),
  });

  // The directory, for names and for the picker. A calendar carries its rep's display name already,
  // so this is only needed to offer reps who have no calendar yet — which is why a caller without
  // `user:read` still gets a usable screen and simply cannot add one.
  const users = useQuery({
    enabled,
    retry: false,
    queryKey: usersKey(subject ?? ""),
    queryFn: ({ signal }) => fetchUsers(accessToken!, signal),
  });

  const failed = [calendars, holidays].find((query) => query.isError);

  if (failed) {
    const error = failed.error;

    return (
      <p role="alert" className="text-sm text-destructive">
        {error instanceof ApiError && error.status === 403 ? t("forbidden") : t("failed")}
      </p>
    );
  }

  if (!calendars.data || !holidays.data) {
    return <p className="text-sm text-muted-foreground">{t("loading")}</p>;
  }

  return (
    <div className="flex flex-col gap-8">
      <Calendars calendars={calendars.data} users={users.data ?? []} />
      <Holidays holidays={holidays.data} />
    </div>
  );
}

/** One row per rep who has a pattern, plus a way to give one to a rep who has not. */
function Calendars({
  calendars,
  users,
}: {
  calendars: readonly WorkingCalendar[];
  users: readonly User[];
}) {
  const t = useTranslations("Calendars");
  const refusals = useTranslations("Refusals");
  const client = useQueryClient();
  const { user } = useAuth();
  const { has } = usePermissions();

  const accessToken = user?.access_token;
  const canWrite = has("journey:write");

  const [adding, setAdding] = useState<string | null>(null);
  const [refused, setRefused] = useState<readonly string[]>([]);

  const complain = (error: unknown, fallback: string) =>
    setRefused(
      error instanceof ApiError && error.problems.length > 0
        ? refusalTexts(refusals, error.problems)
        : [fallback],
    );

  const save = useMutation({
    mutationFn: (row: { userId: string; days: readonly WeekdayName[]; capacity: string }) =>
      setCalendar(accessToken!, row.userId, row.days, Number(row.capacity)),
    onSuccess: async () => {
      setRefused([]);
      setAdding(null);
      await client.invalidateQueries({ queryKey: ["calendars"] });
    },
    onError: (error) => complain(error, t("saveFailed")),
  });

  const remove = useMutation({
    mutationFn: (userId: string) => deleteCalendar(accessToken!, userId),
    onSuccess: async () => {
      setRefused([]);
      await client.invalidateQueries({ queryKey: ["calendars"] });
    },
    onError: (error) => complain(error, t("removeFailed")),
  });

  const configured = new Set(calendars.map((calendar) => calendar.userId));

  // Deactivated reps are not offered: the server refuses a calendar for one, and offering the choice
  // only to take it back is worse than not offering it. An existing calendar for a since-deactivated
  // rep still renders — it is what was planned against, and it is theirs to remove deliberately.
  const unconfigured = users.filter(
    (candidate) => candidate.isActive && !configured.has(candidate.subjectId),
  );

  return (
    <section className="flex flex-col gap-3">
      <header className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <h2 className="text-sm font-semibold">{t("calendarsTitle")}</h2>
          <p className="text-sm text-muted-foreground">{t("calendarsIntro")}</p>
        </div>

        {canWrite && unconfigured.length > 0 ? (
          <Button type="button" size="sm" onClick={() => setAdding(unconfigured[0].subjectId)}>
            <Plus className="size-4" />
            {t("addCalendar")}
          </Button>
        ) : null}
      </header>

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-xl bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      {adding !== null ? (
        <div className="flex flex-wrap items-center gap-3 rounded-xl border border-border p-4">
          <label className="flex items-center gap-2 text-sm">
            <span className="text-muted-foreground">{t("rep")}</span>
            <select
              className={CONTROL}
              value={adding}
              aria-label={t("rep")}
              onChange={(event) => setAdding(event.target.value)}
            >
              {unconfigured.map((candidate) => (
                <option key={candidate.subjectId} value={candidate.subjectId}>
                  {identifying(candidate)}
                </option>
              ))}
            </select>
          </label>

          <CalendarRow
            // Remounted per rep, so switching in the picker reseeds the days rather than carrying
            // the previous rep's week across.
            key={adding}
            calendar={{
              userId: adding,
              displayName: unconfigured.find((c) => c.subjectId === adding)?.displayName ?? null,
              // A five-day week and a modest load: a starting point an admin adjusts, not a rule
              // they inherit — nothing is written until they press Save.
              workingDays: ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
              visitsPerDay: 8,
            }}
            draft
            canWrite={canWrite}
            busy={save.isPending}
            onSave={(row) => save.mutate(row)}
            onRemove={() => setAdding(null)}
          />
        </div>
      ) : null}

      {calendars.length === 0 ? (
        <p className="text-sm text-muted-foreground">{t("noCalendars")}</p>
      ) : (
        <ul className="flex flex-col divide-y divide-border">
          {calendars.map((calendar) => (
            <li key={calendar.userId} className="py-3">
              <CalendarRow
                calendar={calendar}
                canWrite={canWrite}
                busy={save.isPending || remove.isPending}
                onSave={(row) => save.mutate(row)}
                onRemove={() => remove.mutate(calendar.userId)}
              />
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/** One rep's week and capacity, editable in place. */
function CalendarRow({
  calendar,
  draft = false,
  canWrite,
  busy,
  onSave,
  onRemove,
}: {
  calendar: WorkingCalendar;
  draft?: boolean;
  canWrite: boolean;
  busy: boolean;
  onSave: (row: { userId: string; days: readonly WeekdayName[]; capacity: string }) => void;
  onRemove: () => void;
}) {
  const t = useTranslations("Calendars");

  const [days, setDays] = useState<readonly WeekdayName[]>(calendar.workingDays);
  const [capacity, setCapacity] = useState(String(calendar.visitsPerDay));

  const name = calendar.displayName ?? t("unknownRep");

  // The server refuses an empty week, and says why: to stop planning for a rep you remove their
  // calendar. Said here so the Save button explains itself rather than the refusal arriving after.
  const empty = days.length === 0;
  const badCapacity = capacityProblem(capacity);

  const dirty =
    draft
    || capacity !== String(calendar.visitsPerDay)
    || days.length !== calendar.workingDays.length
    || WEEK.some((day) => days.includes(day) !== calendar.workingDays.includes(day));

  return (
    <div className="flex flex-wrap items-center gap-3 text-sm">
      <span className="min-w-40 font-medium">{name}</span>

      <fieldset className="flex flex-wrap items-center gap-1" disabled={!canWrite}>
        <legend className="sr-only">{t("daysFor", { name })}</legend>

        {WEEK.map((day) => {
          const on = days.includes(day);

          return (
            <label
              key={day}
              className={`cursor-pointer rounded-lg border px-2 py-1 text-xs ${
                on ? "border-primary bg-primary/15 text-primary" : "border-border text-muted-foreground"
              } ${!canWrite ? "cursor-default opacity-70" : ""}`}
            >
              <input
                type="checkbox"
                className="sr-only"
                checked={on}
                disabled={!canWrite}
                aria-label={t("dayFor", { day: t(`weekday.${day}`), name })}
                onChange={() =>
                  setDays((current) =>
                    current.includes(day)
                      ? current.filter((other) => other !== day)
                      : // Kept in week order rather than click order, so the stored list reads the
                        // way the row does and a "did this change?" comparison is not about
                        // sequence.
                        WEEK.filter((other) => other === day || current.includes(other)),
                  )
                }
              />
              {t(`weekdayShort.${day}`)}
            </label>
          );
        })}
      </fieldset>

      <label className="flex items-center gap-2">
        <input
          type="text"
          inputMode="numeric"
          className={`${CONTROL} w-14 text-right ${badCapacity ? "border-destructive" : ""}`}
          disabled={!canWrite}
          value={capacity}
          aria-invalid={badCapacity}
          aria-label={t("capacityFor", { name })}
          onChange={(event) => setCapacity(event.target.value)}
        />
        <span className="text-muted-foreground">{t("callsADay")}</span>
      </label>

      {empty && canWrite ? (
        <span className="text-xs text-destructive">{t("noDaysChosen")}</span>
      ) : null}

      {canWrite ? (
        <div className="ml-auto flex gap-2">
          <Button
            type="button"
            size="sm"
            variant={draft ? "default" : "outline"}
            disabled={busy || !dirty || empty || badCapacity}
            onClick={() => onSave({ userId: calendar.userId, days, capacity })}
            aria-label={t("saveCalendarFor", { name })}
          >
            {t("save")}
          </Button>
          <Button
            type="button"
            size="sm"
            variant="outline"
            disabled={busy}
            onClick={onRemove}
            aria-label={draft ? t("discard") : t("removeCalendarFor", { name })}
          >
            {draft ? t("discard") : t("remove")}
          </Button>
        </div>
      ) : null}
    </div>
  );
}

/** The days nobody works, for everybody (`JRN-02`). */
function Holidays({ holidays }: { holidays: readonly Holiday[] }) {
  const t = useTranslations("Calendars");
  const refusals = useTranslations("Refusals");
  const client = useQueryClient();
  const day = useBusinessDay();
  const { user } = useAuth();
  const { has } = usePermissions();

  const accessToken = user?.access_token;
  const canWrite = has("journey:write");

  const [date, setDate] = useState("");
  const [name, setName] = useState("");
  const [refused, setRefused] = useState<readonly string[]>([]);

  const complain = (error: unknown, fallback: string) =>
    setRefused(
      error instanceof ApiError && error.problems.length > 0
        ? refusalTexts(refusals, error.problems)
        : [fallback],
    );

  const add = useMutation({
    mutationFn: () => addHoliday(accessToken!, date, name.trim()),
    onSuccess: async () => {
      setRefused([]);
      setDate("");
      setName("");
      await client.invalidateQueries({ queryKey: ["holidays"] });
    },
    onError: (error) => complain(error, t("holidayFailed")),
  });

  const remove = useMutation({
    mutationFn: (id: string) => deleteHoliday(accessToken!, id),
    onSuccess: async () => {
      setRefused([]);
      await client.invalidateQueries({ queryKey: ["holidays"] });
    },
    onError: (error) => complain(error, t("removeHolidayFailed")),
  });

  // The date is what makes a holiday, so the same date twice is the server's refusal to give. Said
  // here as well because it is the one mistake an admin makes twice — two names for one day.
  const duplicate = holidays.some((holiday) => holiday.date === date);

  return (
    <section className="flex flex-col gap-3">
      <header>
        <h2 className="text-sm font-semibold">{t("holidaysTitle")}</h2>
        <p className="text-sm text-muted-foreground">{t("holidaysIntro")}</p>
      </header>

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-xl bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      {canWrite ? (
        <div className="flex flex-wrap items-end gap-3">
          <label className="flex flex-col gap-1 text-sm">
            <span className="text-muted-foreground">{t("date")}</span>
            <input
              type="date"
              className={`${CONTROL} ${duplicate ? "border-destructive" : ""}`}
              value={date}
              aria-invalid={duplicate}
              aria-label={t("date")}
              onChange={(event) => setDate(event.target.value)}
            />
          </label>

          <label className="flex flex-col gap-1 text-sm">
            <span className="text-muted-foreground">{t("holidayName")}</span>
            <input
              className={CONTROL}
              value={name}
              aria-label={t("holidayName")}
              placeholder={t("holidayPlaceholder")}
              onChange={(event) => setName(event.target.value)}
            />
          </label>

          <Button
            type="button"
            size="sm"
            disabled={add.isPending || date === "" || name.trim() === "" || duplicate}
            onClick={() => add.mutate()}
          >
            <Plus className="size-4" />
            {t("addHoliday")}
          </Button>

          {duplicate ? (
            <span className="pb-2 text-xs text-destructive">{t("holidayTaken")}</span>
          ) : null}
        </div>
      ) : null}

      {holidays.length === 0 ? (
        <p className="text-sm text-muted-foreground">{t("noHolidays")}</p>
      ) : (
        <ul className="flex flex-col divide-y divide-border">
          {holidays.map((holiday) => (
            <li key={holiday.id} className="flex flex-wrap items-center gap-3 py-2 text-sm">
              {/* Rendered in the reader's locale and the caller's timezone, like every other date
                  on this API — the string on the wire is a plain calendar day. */}
              <span className="min-w-32 font-mono text-xs text-muted-foreground">
                {day(holiday.date)}
              </span>
              <span>{holiday.name}</span>

              {canWrite ? (
                <Button
                  type="button"
                  size="sm"
                  variant="outline"
                  className="ml-auto"
                  disabled={remove.isPending}
                  onClick={() => remove.mutate(holiday.id)}
                  aria-label={t("removeHolidayNamed", { name: holiday.name })}
                >
                  {t("remove")}
                </Button>
              ) : null}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
