"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useTranslations } from "next-intl";
import { useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { Button } from "@/components/ui/button";
import { LinkButton } from "@/components/ui/link-button";
import { ApiError } from "@/lib/api/client";
import { refusalTexts } from "@/lib/api/refusals";
import {
  deleteSurvey,
  fetchSurveys,
  surveysKey,
  type SurveyForm,
} from "@/lib/api/surveys";
import { usePermissions } from "@/lib/auth/use-permissions";

/**
 * The survey forms a tenant has defined (`AUD-04`, `CFG-04`) — W10 slice 9b.
 *
 * <b>A list, not an editor.</b> Slice 9a built the editor first because the server refuses a form
 * with no questions, which makes the editor the only thing that can bring one into existence. This
 * is the layer that was missing: somewhere to see what a tenant asks, and the route in.
 *
 * <b>Sorted by the server, by name.</b> Not re-sorted here — a list that arrived in one order and
 * rendered in another is two answers to "which is first", and the API's is the one a second screen
 * would agree with.
 *
 * <b>Deleting is confirmed, and the confirmation says what does *not* happen.</b> That is the
 * opposite shape from the custom-field catalogue's warning and the more surprising fact: answers
 * already given stay in Audit's rows and stay readable, because each one carries its question's text
 * as it was asked. Configuration cannot remove them and could not describe them if it tried
 * (ADR-0005). An administrator hesitating over this button is usually asking "do I lose the
 * history?" — so the sentence answers that rather than asking whether they are sure.
 */
export function SurveyBrowser() {
  const t = useTranslations("SurveyList");
  const refusals = useTranslations("Refusals");
  const { user } = useAuth();
  const client = useQueryClient();
  const { has } = usePermissions();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;

  const surveys = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey: surveysKey(subject ?? ""),
    queryFn: ({ signal }) => fetchSurveys(accessToken!, signal),
  });

  const [confirming, setConfirming] = useState<string | null>(null);
  const [refused, setRefused] = useState<readonly string[]>([]);

  const remove = useMutation({
    mutationFn: (form: SurveyForm) => deleteSurvey(accessToken!, form.id),
    onSuccess: async () => {
      setRefused([]);
      setConfirming(null);
      await client.invalidateQueries({ queryKey: ["surveys"] });
    },
    onError: (error) =>
      setRefused(
        error instanceof ApiError && error.problems.length > 0
          ? refusalTexts(refusals, error.problems)
          : [t("deleteFailed")],
      ),
  });

  const rows = surveys.data ?? [];
  const mayWrite = has("config:write");

  return (
    <div className="flex flex-col gap-4">
      {mayWrite ? (
        <div>
          <LinkButton href="/configuration/surveys/new" size="sm">
            <Plus className="size-4" />
            {t("newSurvey")}
          </LinkButton>
        </div>
      ) : null}

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-xl bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      {surveys.isPending ? (
        <p className="text-sm text-muted-foreground">{t("loading")}</p>
      ) : surveys.isError ? (
        <p role="alert" className="text-sm text-destructive">
          {surveys.error instanceof ApiError && surveys.error.status === 403
            ? t("forbidden")
            : t("failed")}
        </p>
      ) : rows.length === 0 ? (
        <p className="text-sm text-muted-foreground">{t("empty")}</p>
      ) : (
        <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
          {rows.map((form) => {
            const mandatory = form.questions.filter((question) => question.mandatory).length;

            return (
              <li key={form.id} className="flex flex-col gap-2 px-4 py-3 text-sm">
                <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
                  <span className="font-medium">{form.name}</span>

                  {/*
                    The count and how many of them a rep cannot skip. The second number is the one
                    that decides whether an audit step can be finished at all (`BR-AUD-7`), so it is
                    worth seeing without opening the form.
                  */}
                  <span className="text-xs text-muted-foreground">
                    {t("questionCount", { count: form.questions.length })}
                    {mandatory > 0 ? ` · ${t("mandatoryCount", { count: mandatory })}` : ""}
                  </span>

                  <div className="ml-auto flex gap-2">
                    {/* A link, not a button: it navigates, and a reader may want it in a new tab.
                        Offered to a reader too — the editor renders read-only for them. */}
                    <LinkButton
                      href={`/configuration/surveys/${form.id}`}
                      size="sm"
                      variant="outline"
                      aria-label={t("openNamed", { name: form.name })}
                    >
                      {mayWrite ? t("edit") : t("view")}
                    </LinkButton>

                    {mayWrite ? (
                      <Button
                        type="button"
                        size="sm"
                        variant="outline"
                        onClick={() => {
                          setRefused([]);
                          setConfirming(form.id);
                        }}
                        aria-label={t("deleteNamed", { name: form.name })}
                      >
                        {t("delete")}
                      </Button>
                    ) : null}
                  </div>
                </div>

                {confirming === form.id ? (
                  <div
                    role="alert"
                    className="flex flex-col gap-2 rounded-lg bg-muted px-3 py-2 text-xs"
                  >
                    <p>{t("deleteWarning")}</p>
                    <div className="flex gap-2">
                      <Button
                        type="button"
                        size="sm"
                        variant="destructive"
                        disabled={remove.isPending}
                        onClick={() => remove.mutate(form)}
                      >
                        {t("confirmDelete")}
                      </Button>
                      <Button
                        type="button"
                        size="sm"
                        variant="outline"
                        onClick={() => setConfirming(null)}
                      >
                        {t("cancel")}
                      </Button>
                    </div>
                  </div>
                ) : null}
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
