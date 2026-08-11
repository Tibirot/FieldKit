"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Lock, Plus } from "lucide-react";
import { useTranslations } from "next-intl";
import { useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import { refusalTexts } from "@/lib/api/refusals";
import {
  draftScoreWeights,
  fetchScoreWeights,
  publishScoreWeights,
  REQUIRED_HUNDREDTHS,
  SCORE_PILLARS,
  scoreWeightsKey,
  setScoreWeights,
  sumInHundredths,
  type ScorePillar,
  type ScoreWeightSet,
} from "@/lib/api/score-weights";
import { usePermissions } from "@/lib/auth/use-permissions";

const CONTROL =
  "h-9 w-24 rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/** A draft as the author is editing it — every percentage as typed, none parsed. */
type Draft = Record<ScorePillar, string>;

/** An even-ish starting point. Not 33.33 × 3, which is exactly 99.99 and refused. */
const STARTING: Draft = { Availability: "50", ShareOfShelf: "30", PriceCompliance: "20" };

function draftFrom(set: ScoreWeightSet): Draft {
  const draft = { ...STARTING };

  for (const pillar of SCORE_PILLARS) {
    draft[pillar] = String(set.weights.find((weight) => weight.pillar === pillar)?.percentage ?? 0);
  }

  return draft;
}

/** The typed values as the API takes them, in the pillar order the screen shows. */
function weightsOf(draft: Draft) {
  return SCORE_PILLARS.map((pillar) => ({ pillar, percentage: Number(draft[pillar]) }));
}

/** Whether every box holds a number the server would accept at all. */
function typedNumbers(draft: Draft): boolean {
  return SCORE_PILLARS.every((pillar) => {
    const value = Number(draft[pillar]);

    return draft[pillar].trim() !== "" && Number.isFinite(value) && value >= 0 && value <= 100;
  });
}

/**
 * The tenant's perfect-store weighting, by version (`AUD-07`, `BR-AUD-4`, `BR-AUD-8`).
 *
 * <b>The screen's real job is making "publishing is one-way" legible</b>, and it does that by
 * showing rather than warning. A published version has **no edit control at all** — not a disabled
 * one, which is a dead control that explains nothing — and the only thing offered beside it is
 * "start a new version from this one". An administrator who wants to change a published weighting is
 * shown the thing they actually have to do.
 *
 * <b>The running total is the second half.</b> `BR-AUD-4` refuses anything but exactly 100, with no
 * tolerance, so a screen that let somebody type three numbers and press Save would trade a refusal
 * for every near miss. The total says how far off it is while they type, and the save is refused
 * here before it is refused there.
 *
 * <b>Every version stays listed, newest first.</b> Sealed audits point at them forever
 * (`BR-AUD-8`), so a list that hid the old ones would hide the only way to read a historical score.
 */
export function ScoreWeights() {
  const t = useTranslations("ScoreWeights");
  const refusals = useTranslations("Refusals");
  const { user } = useAuth();
  const permissions = usePermissions();
  const queryClient = useQueryClient();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;
  const enabled = Boolean(accessToken && subject);
  const mayWrite = permissions.has("config:write");

  const [draft, setDraft] = useState<Draft | null>(null);

  /** The version being edited, or null when the draft is a brand-new one. */
  const [editing, setEditing] = useState<number | null>(null);
  const [problems, setProblems] = useState<string[]>([]);

  /** Which version the publish confirmation is open for. One at a time, by version number. */
  const [confirming, setConfirming] = useState<number | null>(null);

  const sets = useQuery({
    enabled,
    queryKey: scoreWeightsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchScoreWeights(accessToken!, signal),
  });

  function reset() {
    setDraft(null);
    setEditing(null);
    setProblems([]);
  }

  async function refresh() {
    await queryClient.invalidateQueries({ queryKey: scoreWeightsKey(subject ?? "") });
  }

  function fail(error: unknown) {
    setProblems(
      error instanceof ApiError && error.problems.length > 0
        ? refusalTexts(refusals, error.problems)
        : [t("failed")],
    );
  }

  const save = useMutation({
    mutationFn: async () => {
      const weights = weightsOf(draft!);

      return editing === null
        ? draftScoreWeights(accessToken!, weights)
        : setScoreWeights(accessToken!, editing, weights);
    },
    onSuccess: async () => {
      reset();
      await refresh();
    },
    onError: fail,
  });

  const publish = useMutation({
    mutationFn: (version: number) => publishScoreWeights(accessToken!, version),
    onSuccess: async () => {
      setConfirming(null);
      setProblems([]);
      await refresh();
    },
    onError: (error) => {
      setConfirming(null);
      fail(error);
    },
  });

  if (sets.isError) {
    const error = sets.error;

    return (
      <p role="alert" className="text-sm text-destructive">
        {error instanceof ApiError && error.status === 403 ? t("forbidden") : t("failed")}
      </p>
    );
  }

  if (!sets.data) return <p className="text-sm text-muted-foreground">{t("loading")}</p>;

  const hundredths = draft ? sumInHundredths(weightsOf(draft)) : 0;
  const balanced = draft !== null && typedNumbers(draft) && hundredths === REQUIRED_HUNDREDTHS;

  return (
    <section className="flex flex-col gap-4">
      {problems.length > 0 && (
        <ul role="alert" className="flex flex-col gap-1 text-sm text-destructive">
          {problems.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      )}

      {draft !== null && (
        <form
          className="flex flex-col gap-3 rounded-xl border border-border bg-card p-4"
          onSubmit={(event) => {
            event.preventDefault();
            setProblems([]);
            save.mutate();
          }}
        >
          <h2 className="text-sm font-semibold">
            {editing === null ? t("draftingNew") : t("editing", { version: editing })}
          </h2>

          <div className="flex flex-wrap items-end gap-4">
            {SCORE_PILLARS.map((pillar) => (
              <label key={pillar} className="flex flex-col gap-1 text-sm">
                <span className="text-muted-foreground">{t(`pillars.${pillar}`)}</span>
                <input
                  className={CONTROL}
                  type="number"
                  min={0}
                  max={100}
                  step="0.01"
                  value={draft[pillar]}
                  onChange={(event) =>
                    setDraft({ ...draft, [pillar]: event.target.value })
                  }
                />
              </label>
            ))}
          </div>

          {/*
            The running total, always shown rather than only when wrong.
            A number that appears on failure teaches an administrator that the screen is watching
            them; one that is always there teaches them what the rule is.
          */}
          <p
            className={`text-sm ${balanced ? "text-muted-foreground" : "text-destructive"}`}
            aria-live="polite"
          >
            {t("total", { total: (hundredths / 100).toFixed(2) })}
            {!balanced && typedNumbers(draft) && (
              <span> {t(hundredths > REQUIRED_HUNDREDTHS ? "over" : "under", {
                amount: (Math.abs(hundredths - REQUIRED_HUNDREDTHS) / 100).toFixed(2),
              })}</span>
            )}
          </p>

          <div className="flex gap-2">
            <Button type="submit" size="sm" disabled={!balanced || save.isPending}>
              {t("save")}
            </Button>
            <Button type="button" size="sm" variant="outline" onClick={reset}>
              {t("cancel")}
            </Button>
          </div>
        </form>
      )}

      {mayWrite && draft === null && (
        <div>
          <Button
            size="sm"
            onClick={() => {
              setProblems([]);
              setEditing(null);
              setDraft(STARTING);
            }}
          >
            <Plus aria-hidden className="size-4" />
            {t("newVersion")}
          </Button>
        </div>
      )}

      {sets.data.length === 0 && draft === null && (
        <p className="text-sm text-muted-foreground">{t("empty")}</p>
      )}

      <ul className="flex flex-col gap-3">
        {sets.data.map((set) => (
          <li
            key={set.id}
            className="flex flex-col gap-2 rounded-xl border border-border bg-card p-4"
          >
            <div className="flex flex-wrap items-center gap-2">
              <h3 className="text-sm font-semibold">{t("version", { version: set.version })}</h3>

              {set.isPublished ? (
                <span className="inline-flex items-center gap-1 rounded-full bg-muted px-2 py-0.5 text-[11.5px] text-muted-foreground">
                  <Lock aria-hidden className="size-3" />
                  {t("published")}
                </span>
              ) : (
                <span className="rounded-full bg-amber-100 px-2 py-0.5 text-[11.5px] text-amber-900 dark:bg-amber-950 dark:text-amber-200">
                  {t("draft")}
                </span>
              )}
            </div>

            <dl className="flex flex-wrap gap-x-6 gap-y-1 text-sm">
              {SCORE_PILLARS.map((pillar) => (
                <div key={pillar} className="flex gap-1">
                  <dt className="text-muted-foreground">{t(`pillars.${pillar}`)}</dt>
                  <dd className="font-medium">
                    {set.weights.find((weight) => weight.pillar === pillar)?.percentage ?? 0}%
                  </dd>
                </div>
              ))}
            </dl>

            {mayWrite && (
              <div className="flex flex-wrap items-center gap-2">
                {/*
                  A published version offers no edit control at all — not a disabled one. What it
                  offers instead is the thing an administrator actually has to do, which is the whole
                  of `BR-AUD-8` expressed as a button rather than as a warning.
                */}
                {set.isPublished ? (
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => {
                      setProblems([]);
                      setEditing(null);
                      setDraft(draftFrom(set));
                    }}
                  >
                    {t("copyToNew")}
                  </Button>
                ) : (
                  <>
                    <Button
                      size="sm"
                      variant="outline"
                      onClick={() => {
                        setProblems([]);
                        setEditing(set.version);
                        setDraft(draftFrom(set));
                      }}
                    >
                      {t("edit")}
                    </Button>

                    {confirming === set.version ? (
                      <span className="flex flex-wrap items-center gap-2 text-sm">
                        {/*
                          The confirmation names the cost rather than asking "are you sure". A rep of
                          this pattern elsewhere in the codebase: a prompt that says what cannot be
                          undone is a decision; one that asks for a second click is a speed bump.
                        */}
                        <span className="text-muted-foreground">{t("publishWarning")}</span>
                        <Button
                          size="sm"
                          onClick={() => publish.mutate(set.version)}
                          disabled={publish.isPending}
                        >
                          {t("publishConfirm")}
                        </Button>
                        <Button size="sm" variant="outline" onClick={() => setConfirming(null)}>
                          {t("cancel")}
                        </Button>
                      </span>
                    ) : (
                      <Button
                        size="sm"
                        variant="outline"
                        onClick={() => {
                          setProblems([]);
                          setConfirming(set.version);
                        }}
                      >
                        {t("publish")}
                      </Button>
                    )}
                  </>
                )}
              </div>
            )}
          </li>
        ))}
      </ul>
    </section>
  );
}
