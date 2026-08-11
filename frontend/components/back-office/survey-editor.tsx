"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowDown, ArrowUp, Plus, X } from "lucide-react";
import { useTranslations } from "next-intl";
import { useMemo, useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import { refusalTexts } from "@/lib/api/refusals";
import {
  createSurvey,
  duplicateKeys,
  isChoice,
  MAXIMUM_QUESTIONS,
  QUESTION_TEXT_LIMIT,
  setSurvey,
  SURVEY_NAME_LIMIT,
  SURVEY_QUESTION_TYPES,
  surveysKey,
  fetchSurveys,
  type SurveyForm,
  type SurveyQuestionType,
} from "@/lib/api/surveys";
import { usePermissions } from "@/lib/auth/use-permissions";
import { useRouter } from "@/i18n/navigation";
import { keyFromLabel } from "@/lib/forms/key-from-label";

const CONTROL =
  "h-9 w-full rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
  + " disabled:cursor-not-allowed disabled:opacity-60";

/**
 * One question as the author is editing it.
 *
 * `options` is the textarea's raw text rather than a list, so a half-typed line is not silently
 * dropped between keystrokes. `saved` is what the server last stored for this question, and null for
 * one that has never been saved — the distinction the key rule below turns on.
 */
type Draft = {
  /** A React key of the row's own. Never the index: rows move, and the textareas hold selection. */
  row: string;
  key: string;
  text: string;
  type: SurveyQuestionType;
  mandatory: boolean;
  options: string;
  saved: { key: string; type: SurveyQuestionType } | null;
};

/** What can be wrong with one question, named by the message that says so. */
type Problem = "textMissing" | "keyMissing" | "keyDuplicate" | "optionsMissing" | null;

/**
 * Authoring a tenant's survey form (`AUD-04`, `CFG-04`, `AUD-07`).
 *
 * Loads the form and hands it to an editor seeded once, the shape `PromotionTiers` uses: the whole
 * form is replaced on save, so the draft has to be a full copy rather than a diff.
 */
export function SurveyEditor({ formId }: { formId: string | null }) {
  const t = useTranslations("Surveys");
  const { user } = useAuth();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;
  const enabled = Boolean(accessToken && subject);

  const surveys = useQuery({
    enabled,
    queryKey: surveysKey(subject ?? ""),
    queryFn: ({ signal }) => fetchSurveys(accessToken!, signal),
  });

  if (surveys.isError) {
    const error = surveys.error;

    return (
      <p role="alert" className="text-sm text-destructive">
        {error instanceof ApiError && error.status === 403 ? t("forbidden") : t("failed")}
      </p>
    );
  }

  if (!surveys.data) return <p className="text-sm text-muted-foreground">{t("loading")}</p>;

  const form = formId === null ? null : surveys.data.find((candidate) => candidate.id === formId);

  if (formId !== null && !form) {
    return (
      <p role="alert" className="text-sm text-destructive">
        {t("notFound")}
      </p>
    );
  }

  // Remounted per form so the draft reseeds from what the server holds.
  return <QuestionEditor key={form?.id ?? "new"} form={form ?? null} />;
}

/**
 * The editable half.
 *
 * <b>A question's key is fixed once it has been saved.</b> That is this screen's one real rule, and
 * it is a client-side policy rather than something the API enforces — a `PUT` replaces the questions
 * wholesale and would accept a renamed key without complaint. It is enforced here because an answer
 * is filed under the key (`AUD-09`), Configuration cannot see whether any rep has answered yet
 * (ADR-0005), and so the only safe assumption about a saved question is that somebody has. An admin
 * who wants to ask something different removes the question and adds another, which is the honest
 * description of what they are doing.
 *
 * <b>Order is edited with buttons, not by dragging.</b> The wireframe draws a drag handle; a
 * drag-only reorder cannot be operated from a keyboard and is invisible to a screen reader, and this
 * codebase has spent several slices refusing controls that only work for some people. Buttons are the
 * accessible primitive, they are what the move has to be announced through either way, and a drag
 * handle can be added on top of them later without changing anything below.
 */
function QuestionEditor({ form }: { form: SurveyForm | null }) {
  const t = useTranslations("Surveys");
  // Server refusals, in the reader's language (ADR-0012 stage 2).
  const refusals = useTranslations("Refusals");
  const client = useQueryClient();
  const router = useRouter();
  const { user } = useAuth();
  const permissions = usePermissions();

  const accessToken = user?.access_token;
  const mayWrite = permissions.has("config:write");

  const [name, setName] = useState(form?.name ?? "");
  const [drafts, setDrafts] = useState<Draft[]>(() =>
    (form?.questions ?? []).map((question, index) => ({
      row: `stored-${index}`,
      key: question.key,
      text: question.text,
      type: question.type,
      mandatory: question.mandatory,
      options: question.options.join("\n"),
      saved: { key: question.key, type: question.type },
    })),
  );

  const [refused, setRefused] = useState<readonly string[]>([]);

  /** What the last reorder did, for readers who cannot see the list move. */
  const [moved, setMoved] = useState("");

  const duplicates = useMemo(() => duplicateKeys(drafts), [drafts]);

  const problems = useMemo(
    () =>
      drafts.map((draft): Problem => {
        if (draft.text.trim() === "") return "textMissing";
        // Only reachable by a text with no letters in it at all — "???" derives to nothing. The
        // server refuses it as a malformed key, which says nothing about the question it is on.
        if (draft.key.trim() === "") return "keyMissing";
        if (duplicates.has(draft.key.trim())) return "keyDuplicate";

        return isChoice(draft.type) && options(draft.options).length === 0
          ? "optionsMissing"
          : null;
      }),
    [drafts, duplicates],
  );

  const broken = drafts.length === 0 || problems.some((problem) => problem !== null)
    || name.trim() === "";

  const save = useMutation({
    mutationFn: () => {
      const questions = drafts.map((draft) => ({
        key: draft.key.trim(),
        text: draft.text.trim(),
        type: draft.type,
        mandatory: draft.mandatory,
        // Sent only for the types that can carry them. The server drops them anyway; sending them
        // would leave the wire saying something the stored form does not.
        options: isChoice(draft.type) ? options(draft.options) : null,
      }));

      return form
        ? setSurvey(accessToken!, form.id, name.trim(), questions)
        : createSurvey(accessToken!, name.trim(), questions);
    },

    onSuccess: async (saved) => {
      setRefused([]);
      await client.invalidateQueries({ queryKey: ["surveys"] });

      // A form that has just been created is no longer new, and leaving the screen on `/new` would
      // have the next Save create a second one.
      if (!form) router.replace(`/configuration/surveys/${saved.id}`);
    },

    onError: (error) =>
      setRefused(
        error instanceof ApiError && error.problems.length > 0
          ? refusalTexts(refusals, error.problems)
          : [t("saveFailed")],
      ),
  });

  function move(from: number, to: number) {
    setDrafts((current) => {
      const next = [...current];
      const [question] = next.splice(from, 1);

      next.splice(to, 0, question);
      setMoved(t("movedTo", { text: question.text.trim() || t("untitled"), position: to + 1 }));

      return next;
    });
  }

  function update(index: number, change: Partial<Draft>) {
    setDrafts((current) =>
      current.map((draft, at) => (at === index ? { ...draft, ...change } : draft)),
    );
  }

  return (
    <div className="flex flex-col gap-6">
      {refused.length > 0 ? (
        <ul role="alert" className="rounded-xl bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      <div className="flex max-w-md flex-col gap-1.5">
        <label htmlFor="surveyName" className="text-sm font-medium">
          {t("name")}
        </label>
        <input
          id="surveyName"
          className={CONTROL}
          maxLength={SURVEY_NAME_LIMIT}
          disabled={!mayWrite}
          value={name}
          onChange={(event) => setName(event.target.value)}
        />
        <p className="text-xs text-muted-foreground">{t("nameHint")}</p>
      </div>

      <section className="flex flex-col gap-3">
        <h2 className="text-sm font-semibold">{t("questions")}</h2>

        {/* The reorder, said out loud. The list moving is the whole feedback for a sighted user and
            none at all for anybody else. */}
        <p role="status" aria-live="polite" className="sr-only">
          {moved}
        </p>

        {drafts.length === 0 ? (
          <p className="text-sm text-muted-foreground">{t("noQuestions")}</p>
        ) : (
          <ol className="flex flex-col gap-3">
            {drafts.map((draft, index) => (
              <li
                key={draft.row}
                className="flex flex-col gap-3 rounded-xl border border-border bg-card p-4"
              >
                <div className="flex items-center gap-2">
                  <span className="text-xs font-medium text-muted-foreground">
                    {t("position", { position: index + 1 })}
                  </span>

                  {mayWrite ? (
                    <div className="ml-auto flex items-center gap-1">
                      <button
                        type="button"
                        className="rounded-md p-1 text-muted-foreground hover:text-foreground disabled:opacity-40"
                        disabled={index === 0}
                        aria-label={t("moveUp", { position: index + 1 })}
                        onClick={() => move(index, index - 1)}
                      >
                        <ArrowUp className="size-4" />
                      </button>
                      <button
                        type="button"
                        className="rounded-md p-1 text-muted-foreground hover:text-foreground disabled:opacity-40"
                        disabled={index === drafts.length - 1}
                        aria-label={t("moveDown", { position: index + 1 })}
                        onClick={() => move(index, index + 1)}
                      >
                        <ArrowDown className="size-4" />
                      </button>
                      <button
                        type="button"
                        className="rounded-md p-1 text-muted-foreground hover:text-foreground"
                        aria-label={t("remove", { position: index + 1 })}
                        onClick={() =>
                          setDrafts((current) => current.filter((_, at) => at !== index))
                        }
                      >
                        <X className="size-4" />
                      </button>
                    </div>
                  ) : null}
                </div>

                <div className="grid gap-3 sm:grid-cols-2">
                  <div className="flex flex-col gap-1.5">
                    <label htmlFor={`text-${draft.row}`} className="text-sm font-medium">
                      {t("questionText")}
                    </label>
                    <input
                      id={`text-${draft.row}`}
                      className={CONTROL}
                      maxLength={QUESTION_TEXT_LIMIT}
                      disabled={!mayWrite}
                      value={draft.text}
                      aria-invalid={problems[index] === "textMissing"}
                      onChange={(event) =>
                        update(index, {
                          text: event.target.value,
                          // Only while the question is new. On a saved one the key is what answers
                          // are filed under, and following the text would re-file them.
                          ...(draft.saved ? {} : { key: keyFromLabel(event.target.value) }),
                        })
                      }
                    />
                  </div>

                  <div className="flex flex-col gap-1.5">
                    <label htmlFor={`key-${draft.row}`} className="text-sm font-medium">
                      {t("questionKey")}
                    </label>
                    <input
                      id={`key-${draft.row}`}
                      className={`${CONTROL} font-mono`}
                      spellCheck={false}
                      // Fixed once saved. Disabled rather than hidden: the key is what a report
                      // groups by, so an admin has every reason to read it and none to change it.
                      disabled={!mayWrite || draft.saved !== null}
                      value={draft.key}
                      aria-invalid={
                        problems[index] === "keyMissing" || problems[index] === "keyDuplicate"
                      }
                      onChange={(event) => update(index, { key: event.target.value })}
                    />
                    <p className="text-xs text-muted-foreground">
                      {draft.saved ? t("keyFixed") : t("keyHint")}
                    </p>
                  </div>

                  <div className="flex flex-col gap-1.5">
                    <label htmlFor={`type-${draft.row}`} className="text-sm font-medium">
                      {t("questionType")}
                    </label>
                    <select
                      id={`type-${draft.row}`}
                      className={CONTROL}
                      disabled={!mayWrite}
                      value={draft.type}
                      onChange={(event) =>
                        update(index, { type: event.target.value as SurveyQuestionType })
                      }
                    >
                      {SURVEY_QUESTION_TYPES.map((type) => (
                        <option key={type} value={type}>
                          {t(`types.${type}`)}
                        </option>
                      ))}
                    </select>

                    {/* Allowed, and worth saying. The answers already filed under this key were of
                        the old shape, and nothing rewrites them — so a report reading this key after
                        the change reads two kinds of answer. */}
                    {draft.saved && draft.saved.type !== draft.type ? (
                      <p className="text-xs text-amber-700 dark:text-amber-500">
                        {t("typeChanged", { type: t(`types.${draft.saved.type}`) })}
                      </p>
                    ) : null}
                  </div>

                  <label className="flex items-center gap-2 self-end pb-2 text-sm font-medium">
                    <input
                      type="checkbox"
                      className="size-4 accent-primary"
                      disabled={!mayWrite}
                      checked={draft.mandatory}
                      onChange={(event) => update(index, { mandatory: event.target.checked })}
                    />
                    {t("mandatory")}
                  </label>
                </div>

                {/* Only for the types that offer a list. Options beside a number question would be a
                    rule an admin could reasonably expect to apply and nothing would enforce. */}
                {isChoice(draft.type) ? (
                  <div className="flex flex-col gap-1.5">
                    <label htmlFor={`options-${draft.row}`} className="text-sm font-medium">
                      {t("options")}
                    </label>
                    <textarea
                      id={`options-${draft.row}`}
                      rows={3}
                      className={`${CONTROL} h-auto py-2 font-mono`}
                      spellCheck={false}
                      disabled={!mayWrite}
                      value={draft.options}
                      aria-invalid={problems[index] === "optionsMissing"}
                      onChange={(event) => update(index, { options: event.target.value })}
                    />
                    <p className="text-xs text-muted-foreground">{t("optionsHint")}</p>
                  </div>
                ) : null}

                {problems[index] ? (
                  <p className="text-xs text-destructive">{t(problems[index]!)}</p>
                ) : null}
              </li>
            ))}
          </ol>
        )}

        {mayWrite ? (
          <div>
            <Button
              type="button"
              size="sm"
              variant="outline"
              disabled={drafts.length >= MAXIMUM_QUESTIONS}
              onClick={() =>
                setDrafts((current) => [
                  ...current,
                  {
                    row: `added-${current.length}-${current.at(-1)?.row ?? "first"}`,
                    key: "",
                    text: "",
                    type: "Text",
                    mandatory: false,
                    options: "",
                    saved: null,
                  },
                ])
              }
            >
              <Plus className="size-4" />
              {t("addQuestion")}
            </Button>

            {drafts.length >= MAXIMUM_QUESTIONS ? (
              <p className="mt-1 text-xs text-muted-foreground">
                {t("atMost", { count: MAXIMUM_QUESTIONS })}
              </p>
            ) : null}
          </div>
        ) : null}
      </section>

      {mayWrite ? (
        <div className="flex items-center gap-3">
          <Button type="button" size="sm" disabled={broken || save.isPending} onClick={() => save.mutate()}>
            {save.isPending ? t("saving") : t("save")}
          </Button>

          {/* An empty form is refused by the server, and it is the one refusal an admin meets by
              doing nothing rather than by doing something wrong. */}
          {drafts.length === 0 ? (
            <span className="text-xs text-muted-foreground">{t("needsAQuestion")}</span>
          ) : broken ? (
            <span className="text-xs text-destructive">{t("fixFirst")}</span>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}

/** The textarea's lines as the API takes them: trimmed, blank lines dropped. */
function options(raw: string): string[] {
  return raw
    .split("\n")
    .map((option) => option.trim())
    .filter((option) => option.length > 0);
}
