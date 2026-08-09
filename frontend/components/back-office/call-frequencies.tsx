"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useTranslations } from "next-intl";
import { useRef, useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { OutletPicker, useAssignedOutlets, type OutletPick } from "@/components/back-office/outlet-picker";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import {
  deleteOutletFrequency,
  deleteSegmentFrequency,
  fetchOutletFrequencies,
  fetchSegmentFrequencies,
  frequencyProblem,
  outletFrequenciesKey,
  segmentFrequenciesKey,
  setOutletFrequency,
  setSegmentFrequency,
  type OutletFrequency,
  type SegmentFrequency,
} from "@/lib/api/journeys";
import { refusalTexts } from "@/lib/api/refusals";
import { usePermissions } from "@/lib/auth/use-permissions";

const CONTROL =
  "h-8 rounded-lg border border-input bg-background px-2 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/** A rule as the author is editing it — both numbers as typed, neither parsed. */
type Row = {
  /** The segment label or the outlet id. Empty for a segment rule nobody has named yet. */
  key: string;
  visits: string;
  cycleDays: string;
  /** Whether the server already holds this rule, which is what makes Remove meaningful. */
  stored: boolean;
};

/**
 * How often each shop is called on (`JRN-01`).
 *
 * **Two rules, and the second is an exception to the first.** A segment's frequency covers every
 * shop in it; an outlet's overrides that for one shop. Nothing else has an opinion, so a shop with
 * neither is simply not planned — which is a real state and not a misconfiguration, and the reason
 * generation reports it as a shortfall rather than inventing a default.
 *
 * **Saved per rule, not per screen.** Each row is its own PUT, keyed by the segment or the outlet.
 * A "save everything" button would make one bad row refuse a screenful of good ones, and the API's
 * per-key idempotence is what makes retrying a single row safe.
 */
export function CallFrequencies() {
  const t = useTranslations("Frequencies");
  const { user } = useAuth();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;
  const enabled = Boolean(accessToken && subject);

  const segments = useQuery({
    enabled,
    queryKey: segmentFrequenciesKey(subject ?? ""),
    queryFn: ({ signal }) => fetchSegmentFrequencies(accessToken!, signal),
  });

  const outlets = useQuery({
    enabled,
    queryKey: outletFrequenciesKey(subject ?? ""),
    queryFn: ({ signal }) => fetchOutletFrequencies(accessToken!, signal),
  });

  const failed = [segments, outlets].find((query) => query.isError);

  if (failed) {
    const error = failed.error;

    return (
      <p role="alert" className="text-sm text-destructive">
        {error instanceof ApiError && error.status === 403 ? t("forbidden") : t("failed")}
      </p>
    );
  }

  if (!segments.data || !outlets.data) {
    return <p className="text-sm text-muted-foreground">{t("loading")}</p>;
  }

  return (
    <div className="flex flex-col gap-8">
      <SegmentRules rules={segments.data} />
      <OutletRules rules={outlets.data} />
    </div>
  );
}

/** The defaults: one rule per segment, covering every shop in it. */
function SegmentRules({ rules }: { rules: readonly SegmentFrequency[] }) {
  const t = useTranslations("Frequencies");
  const refusals = useTranslations("Refusals");
  const client = useQueryClient();
  const { user } = useAuth();
  const { has } = usePermissions();

  const accessToken = user?.access_token;
  const canWrite = has("journey:write");

  // Drafts carry an id of their own rather than being identified by what has been typed into them.
  // Keying them by the segment looked right and was not: a draft is created empty, so the moment it
  // saved there was nothing matching the name it had just been given, and the row stayed on screen
  // beside the rule it had become — announcing that the segment was taken. Found in the browser.
  const [drafts, setDrafts] = useState<readonly { id: number }[]>([]);
  const nextDraft = useRef(0);
  const [refused, setRefused] = useState<readonly string[]>([]);

  const invalidate = () => client.invalidateQueries({ queryKey: ["frequencies"] });

  const complain = (error: unknown, fallback: string) =>
    setRefused(
      error instanceof ApiError && error.problems.length > 0
        ? refusalTexts(refusals, error.problems)
        : [fallback],
    );

  const save = useMutation({
    mutationFn: ({ row }: { row: Row; draft?: number }) =>
      setSegmentFrequency(accessToken!, row.key.trim(), {
        visitsPerCycle: Number(row.visits),
        cycleLengthDays: Number(row.cycleDays),
      }),
    onSuccess: async (_saved, { draft }) => {
      setRefused([]);
      if (draft !== undefined) {
        setDrafts((current) => current.filter((row) => row.id !== draft));
      }
      await invalidate();
    },
    onError: (error) => complain(error, t("saveFailed")),
  });

  const remove = useMutation({
    mutationFn: (segment: string) => deleteSegmentFrequency(accessToken!, segment),
    onSuccess: async () => {
      setRefused([]);
      await invalidate();
    },
    onError: (error) => complain(error, t("removeFailed")),
  });

  return (
    <section className="flex flex-col gap-3">
      <header className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <h2 className="text-sm font-semibold">{t("segmentsTitle")}</h2>
          <p className="text-sm text-muted-foreground">{t("segmentsIntro")}</p>
        </div>
        {canWrite ? (
          <Button
            type="button"
            size="sm"
            onClick={() =>
              setDrafts((current) => [...current, { id: nextDraft.current++ }])
            }
          >
            <Plus className="size-4" />
            {t("addSegment")}
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

      {rules.length === 0 && drafts.length === 0 ? (
        <p className="text-sm text-muted-foreground">{t("noSegments")}</p>
      ) : null}

      <ul className="flex flex-col divide-y divide-border">
        {rules.map((rule) => (
          <SegmentRow
            key={rule.segment}
            rule={rule}
            canWrite={canWrite}
            busy={save.isPending || remove.isPending}
            onSave={(row) => save.mutate({ row })}
            onRemove={() => remove.mutate(rule.segment)}
          />
        ))}

        {drafts.map((draft) => (
          <DraftSegmentRow
            key={`draft-${draft.id}`}
            canWrite={canWrite}
            busy={save.isPending}
            taken={rules.map((rule) => rule.segment)}
            onSave={(row) => save.mutate({ row, draft: draft.id })}
            onDiscard={() =>
              setDrafts((current) => current.filter((row) => row.id !== draft.id))
            }
          />
        ))}
      </ul>
    </section>
  );
}

/** One stored segment rule, editable in place. */
function SegmentRow({
  rule,
  canWrite,
  busy,
  onSave,
  onRemove,
}: {
  rule: SegmentFrequency;
  canWrite: boolean;
  busy: boolean;
  onSave: (row: Row) => void;
  onRemove: () => void;
}) {
  const t = useTranslations("Frequencies");

  const [visits, setVisits] = useState(String(rule.visitsPerCycle));
  const [cycleDays, setCycleDays] = useState(String(rule.cycleLengthDays));

  const problem = frequencyProblem(visits, cycleDays);
  const dirty =
    visits !== String(rule.visitsPerCycle) || cycleDays !== String(rule.cycleLengthDays);

  return (
    <li className="flex flex-wrap items-center gap-3 py-2.5 text-sm">
      <span className="min-w-24 font-mono text-xs">{rule.segment}</span>

      <Numbers
        label={rule.segment}
        visits={visits}
        cycleDays={cycleDays}
        problem={problem}
        canWrite={canWrite}
        onVisits={setVisits}
        onCycleDays={setCycleDays}
      />

      {canWrite ? (
        <div className="ml-auto flex gap-2">
          <Button
            type="button"
            size="sm"
            variant="outline"
            disabled={busy || !dirty || problem !== null}
            onClick={() => onSave({ key: rule.segment, visits, cycleDays, stored: true })}
            aria-label={t("saveSegmentNamed", { segment: rule.segment })}
          >
            {t("save")}
          </Button>
          <Button
            type="button"
            size="sm"
            variant="outline"
            disabled={busy}
            onClick={onRemove}
            aria-label={t("removeSegmentNamed", { segment: rule.segment })}
          >
            {t("remove")}
          </Button>
        </div>
      ) : null}
    </li>
  );
}

/** A segment rule being written, which needs its label choosing before it can be saved. */
function DraftSegmentRow({
  canWrite,
  busy,
  taken,
  onSave,
  onDiscard,
}: {
  canWrite: boolean;
  busy: boolean;
  taken: readonly string[];
  onSave: (row: Row) => void;
  onDiscard: () => void;
}) {
  const t = useTranslations("Frequencies");

  const [segment, setSegment] = useState("");
  const [visits, setVisits] = useState("1");
  const [cycleDays, setCycleDays] = useState("7");

  const trimmed = segment.trim();

  // Said here rather than left to the server, because the server's answer would be a 409 about a
  // rule the admin thought they were creating. Case-insensitive, like the segment labels themselves.
  const duplicate = taken.some((existing) => existing.toLowerCase() === trimmed.toLowerCase());
  const problem = frequencyProblem(visits, cycleDays);

  return (
    <li className="flex flex-wrap items-center gap-3 py-2.5 text-sm">
      <input
        className={`${CONTROL} w-28 ${duplicate ? "border-destructive" : ""}`}
        value={segment}
        aria-label={t("segmentLabel")}
        aria-invalid={duplicate}
        placeholder={t("segmentPlaceholder")}
        onChange={(event) => setSegment(event.target.value)}
      />

      <Numbers
        label={t("newSegment")}
        visits={visits}
        cycleDays={cycleDays}
        problem={problem}
        canWrite={canWrite}
        onVisits={setVisits}
        onCycleDays={setCycleDays}
      />

      {duplicate ? <span className="text-xs text-destructive">{t("segmentTaken")}</span> : null}

      <div className="ml-auto flex gap-2">
        <Button
          type="button"
          size="sm"
          disabled={busy || trimmed === "" || duplicate || problem !== null}
          onClick={() => onSave({ key: trimmed, visits, cycleDays, stored: false })}
        >
          {t("save")}
        </Button>
        <Button type="button" size="sm" variant="outline" onClick={onDiscard}>
          {t("discard")}
        </Button>
      </div>
    </li>
  );
}

/**
 * The shops that depart from their segment (`JRN-01`).
 *
 * The picker says *which* shops have an exception; the rows say *how*. Adding one here does not
 * write anything — an override with numbers nobody chose is a rule nobody meant — so a new shop
 * appears as an unsaved row and stays that way until its numbers are set.
 */
function OutletRules({ rules }: { rules: readonly OutletFrequency[] }) {
  const t = useTranslations("Frequencies");
  const refusals = useTranslations("Refusals");
  const client = useQueryClient();
  const { user } = useAuth();
  const { has } = usePermissions();

  const accessToken = user?.access_token;
  const canWrite = has("journey:write");

  const [drafts, setDrafts] = useState<readonly OutletPick[]>([]);
  const [refused, setRefused] = useState<readonly string[]>([]);

  const stored = rules.map((rule) => rule.outletId);
  const { outlets: named } = useAssignedOutlets(stored, t("unknownOutlet"), Boolean(accessToken));

  const complain = (error: unknown, fallback: string) =>
    setRefused(
      error instanceof ApiError && error.problems.length > 0
        ? refusalTexts(refusals, error.problems)
        : [fallback],
    );

  const save = useMutation({
    mutationFn: (row: Row) =>
      setOutletFrequency(accessToken!, row.key, {
        visitsPerCycle: Number(row.visits),
        cycleLengthDays: Number(row.cycleDays),
      }),
    onSuccess: async (_saved, row) => {
      setRefused([]);
      setDrafts((current) => current.filter((draft) => draft.id !== row.key));
      await client.invalidateQueries({ queryKey: ["frequencies"] });
    },
    onError: (error) => complain(error, t("saveFailed")),
  });

  const remove = useMutation({
    mutationFn: (outletId: string) => deleteOutletFrequency(accessToken!, outletId),
    onSuccess: async () => {
      setRefused([]);
      await client.invalidateQueries({ queryKey: ["frequencies"] });
    },
    onError: (error) => complain(error, t("removeFailed")),
  });

  return (
    <section className="flex flex-col gap-3">
      <header>
        <h2 className="text-sm font-semibold">{t("outletsTitle")}</h2>
        <p className="text-sm text-muted-foreground">{t("outletsIntro")}</p>
      </header>

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-xl bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      {canWrite ? (
        <OutletPicker
          // Nothing is "chosen" as far as the picker is concerned, so it renders as a search box and
          // nothing else. Passing the drafts here — the obvious thing, and what this did first — put
          // each pending shop on screen three times: as a chip, as a disabled search result, and as
          // the row where its numbers are actually set. Two of those had a control that removed it,
          // and they did not mean the same thing. Found in the browser.
          chosen={[]}
          onChange={(next) =>
            setDrafts((current) => [
              ...current,
              // A shop that already has a row — stored or draft — is not added twice. The picker
              // cannot grey it out without being told what is chosen, so the row below is what says
              // it is already there.
              ...next.filter(
                (outlet) =>
                  !stored.includes(outlet.id)
                  && !current.some((draft) => draft.id === outlet.id),
              ),
            ])
          }
          canWrite={canWrite}
          labels={{
            search: t("findOutlet"),
            searchPlaceholder: t("findOutletPlaceholder"),
            noMatches: (search) => t("noOutletMatches", { search }),
            add: t("addOverride"),
            added: t("overridesAdded"),
            addNamed: (outlet) => t("addOverrideNamed", { name: outlet.name }),
            removeNamed: (outlet) => t("discardOverrideNamed", { name: outlet.name }),
          }}
        />
      ) : null}

      {rules.length === 0 && drafts.length === 0 ? (
        <p className="text-sm text-muted-foreground">{t("noOutlets")}</p>
      ) : null}

      <ul className="flex flex-col divide-y divide-border">
        {rules.map((rule, index) => (
          <OutletRow
            key={rule.outletId}
            name={named[index]?.name ?? t("unknownOutlet")}
            rule={rule}
            canWrite={canWrite}
            busy={save.isPending || remove.isPending}
            onSave={(row) => save.mutate(row)}
            onRemove={() => remove.mutate(rule.outletId)}
          />
        ))}

        {drafts.map((draft) => (
          <OutletRow
            key={`draft-${draft.id}`}
            name={draft.name}
            rule={{ outletId: draft.id, visitsPerCycle: 1, cycleLengthDays: 7 }}
            draft
            canWrite={canWrite}
            busy={save.isPending}
            onSave={(row) => save.mutate(row)}
            onRemove={() => setDrafts((current) => current.filter((row) => row.id !== draft.id))}
          />
        ))}
      </ul>
    </section>
  );
}

/** One shop's override — stored, or a draft that has not been written yet. */
function OutletRow({
  name,
  rule,
  draft = false,
  canWrite,
  busy,
  onSave,
  onRemove,
}: {
  name: string;
  rule: OutletFrequency;
  draft?: boolean;
  canWrite: boolean;
  busy: boolean;
  onSave: (row: Row) => void;
  onRemove: () => void;
}) {
  const t = useTranslations("Frequencies");

  const [visits, setVisits] = useState(String(rule.visitsPerCycle));
  const [cycleDays, setCycleDays] = useState(String(rule.cycleLengthDays));

  const problem = frequencyProblem(visits, cycleDays);
  const dirty =
    draft || visits !== String(rule.visitsPerCycle) || cycleDays !== String(rule.cycleLengthDays);

  return (
    <li className="flex flex-wrap items-center gap-3 py-2.5 text-sm">
      <span className="min-w-40 font-medium">{name}</span>

      {draft ? (
        <span className="rounded-full bg-muted px-2 py-0.5 text-xs text-muted-foreground">
          {t("unsaved")}
        </span>
      ) : null}

      <Numbers
        label={name}
        visits={visits}
        cycleDays={cycleDays}
        problem={problem}
        canWrite={canWrite}
        onVisits={setVisits}
        onCycleDays={setCycleDays}
      />

      {canWrite ? (
        <div className="ml-auto flex gap-2">
          <Button
            type="button"
            size="sm"
            variant={draft ? "default" : "outline"}
            disabled={busy || !dirty || problem !== null}
            onClick={() => onSave({ key: rule.outletId, visits, cycleDays, stored: !draft })}
            aria-label={t("saveOutletNamed", { name })}
          >
            {t("save")}
          </Button>
          <Button
            type="button"
            size="sm"
            variant="outline"
            disabled={busy}
            onClick={onRemove}
            aria-label={draft ? t("discardOverrideNamed", { name }) : t("removeOutletNamed", { name })}
          >
            {draft ? t("discard") : t("remove")}
          </Button>
        </div>
      ) : null}
    </li>
  );
}

/**
 * The pair of numbers a frequency is.
 *
 * Rendered as "n visits every d days" rather than as a chosen period, because that is what the API
 * stores and what generation reads — a dropdown of weekly/fortnightly/monthly would be a vocabulary
 * this system does not have, and "2 every 14 days" would have nowhere to go.
 */
function Numbers({
  label,
  visits,
  cycleDays,
  problem,
  canWrite,
  onVisits,
  onCycleDays,
}: {
  label: string;
  visits: string;
  cycleDays: string;
  problem: "visits" | "cycle" | null;
  canWrite: boolean;
  onVisits: (value: string) => void;
  onCycleDays: (value: string) => void;
}) {
  const t = useTranslations("Frequencies");

  return (
    <span className="flex flex-wrap items-center gap-2">
      <input
        type="text"
        inputMode="numeric"
        className={`${CONTROL} w-14 text-right ${problem === "visits" ? "border-destructive" : ""}`}
        disabled={!canWrite}
        value={visits}
        aria-invalid={problem === "visits"}
        aria-label={t("visitsFor", { name: label })}
        onChange={(event) => onVisits(event.target.value)}
      />
      <span className="text-muted-foreground">{t("visitsEvery")}</span>
      <input
        type="text"
        inputMode="numeric"
        className={`${CONTROL} w-16 text-right ${problem === "cycle" ? "border-destructive" : ""}`}
        disabled={!canWrite}
        value={cycleDays}
        aria-invalid={problem === "cycle"}
        aria-label={t("cycleFor", { name: label })}
        onChange={(event) => onCycleDays(event.target.value)}
      />
      <span className="text-muted-foreground">{t("days")}</span>
    </span>
  );
}
