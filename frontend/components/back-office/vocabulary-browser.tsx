"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Percent, Plus } from "lucide-react";
import { useTranslations } from "next-intl";
import { useMemo, useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { Button } from "@/components/ui/button";
import { LinkButton } from "@/components/ui/link-button";
import { ApiError } from "@/lib/api/client";
import { refusalTexts } from "@/lib/api/refusals";
import {
  createVocabulary,
  deleteVocabulary,
  updateVocabulary,
  withAncestry,
  type Category,
  type Vocabulary,
  type VocabularyKind,
} from "@/lib/api/products";
import { usePermissions } from "@/lib/auth/use-permissions";

const CONTROL =
  "h-9 w-full rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/** A category being edited, so the parent select can exclude the subtree it would move into. */
type Editing = (Vocabulary & { parentId?: string | null }) | "new" | null;

/**
 * One of the three classification vocabularies (`PRD-01`).
 *
 * **One component for all three, because the API gives all three the same shape** — `{ id, name }`
 * with create, rename and delete. A category adds a parent and nothing else, so it arrives as an
 * optional prop rather than a second component: three near-copies would drift, and the difference
 * that matters is one nullable field.
 *
 * That is deliberately a *specific* parameter rather than a general "extra fields" escape hatch.
 * There is exactly one vocabulary with a parent and there is unlikely ever to be a second, so a slot
 * that could hold anything would be an abstraction built for a case nobody has.
 */
export function VocabularyBrowser({
  kind,
  entries,
  hierarchical = false,
}: {
  kind: VocabularyKind;
  entries: readonly (Vocabulary & { parentId?: string | null })[];
  /** Renders a parent select, and labels rows with their ancestry. Categories only. */
  hierarchical?: boolean;
}) {
  const t = useTranslations("Classification");

  // Server refusals, in the reader's language (ADR-0012 stage 2).
  const refusals = useTranslations("Refusals");
  const { user } = useAuth();
  const client = useQueryClient();
  const { has } = usePermissions();

  const accessToken = user?.access_token;

  const [editing, setEditing] = useState<Editing>(null);
  const [name, setName] = useState("");
  const [parentId, setParentId] = useState("");
  const [refused, setRefused] = useState<readonly string[]>([]);

  const paths = useMemo(
    () => (hierarchical ? withAncestry(entries as readonly Category[]) : []),
    [entries, hierarchical],
  );

  const pathOf = useMemo(
    () => new Map(paths.map((entry) => [entry.id, entry.path])),
    [paths],
  );

  /**
   * Parents this entry may be moved under.
   *
   * Itself excluded, because a category cannot be its own parent. **Its descendants are not**, and
   * that is on purpose: the API refuses a move into its own subtree with a reason
   * (`product.category.cycle`), and hiding the option would leave someone hunting for a category
   * that is visibly in the list. A refusal that explains beats a control that quietly omits.
   */
  const parentOptions = useMemo(
    () => (editing === "new" || editing === null
      ? paths
      : paths.filter((entry) => entry.id !== editing.id)),
    [paths, editing],
  );

  function open(entry: Editing) {
    setEditing(entry);
    setRefused([]);
    setName(entry === "new" || entry === null ? "" : entry.name);
    setParentId(entry === "new" || entry === null ? "" : (entry.parentId ?? ""));
  }

  function refuse(error: unknown, fallback: string) {
    // The API's own words when it has any: "12 product(s) are branded 'Veridian'. Reclassify them
    // first." names the count and the next step. "Could not delete" names neither.
    setRefused(
      error instanceof ApiError && error.problems.length > 0
        ? refusalTexts(refusals, error.problems)
        : [fallback],
    );
  }

  async function settle() {
    setRefused([]);
    setEditing(null);

    // Every vocabulary is a dropdown on the product form as well as a list here, so the prefix
    // covers both without enumerating callers. Products too: a rename changes what a row displays.
    await client.invalidateQueries({ queryKey: [kind] });
    await client.invalidateQueries({ queryKey: ["products"] });
  }

  const save = useMutation({
    mutationFn: () =>
      editing !== null && editing !== "new"
        ? updateVocabulary(accessToken!, kind, editing.id, {
            name,
            ...(hierarchical ? { parentId: parentId === "" ? null : parentId } : {}),
          })
        : createVocabulary(accessToken!, kind, {
            name,
            ...(hierarchical ? { parentId: parentId === "" ? null : parentId } : {}),
          }),

    onSuccess: settle,
    onError: (error) => refuse(error, t("saveFailed")),
  });

  const remove = useMutation({
    mutationFn: (entry: Vocabulary) => deleteVocabulary(accessToken!, kind, entry.id),
    onSuccess: settle,
    onError: (error) => refuse(error, t("deleteFailed")),
  });

  const canWrite = has("product:write");
  const rows = hierarchical
    ? [...entries].sort((a, b) => (pathOf.get(a.id) ?? "").localeCompare(pathOf.get(b.id) ?? ""))
    : entries;

  return (
    <section className="flex flex-col gap-3">
      {/* Title and button on one row, blurb underneath — rather than all three in one wrapping flex.
          In the browser the tax-class blurb is long enough to push its button onto a second line
          while the other two sat inline, so three sections of the same shape looked like three
          different layouts. Taking the prose out of the row makes the alignment independent of how
          much any one of them has to say. */}
      <div className="flex flex-col gap-1">
        <div className="flex items-center gap-3">
          <h2 className="text-sm font-semibold">{t(`${kind}.title`)}</h2>

          {canWrite ? (
            <Button
              type="button"
              size="sm"
              variant="outline"
              className="ml-auto shrink-0"
              onClick={() => open("new")}
            >
              <Plus className="size-4" />
              {t(`${kind}.new`)}
            </Button>
          ) : null}
        </div>

        <p className="text-xs text-muted-foreground">{t(`${kind}.intro`)}</p>
      </div>

      {editing !== null ? (
        <form
          noValidate
          onSubmit={(event) => {
            event.preventDefault();
            save.mutate();
          }}
          className="flex flex-col gap-3 rounded-xl border border-border p-4"
        >
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="flex flex-col gap-1.5">
              <label htmlFor={`${kind}-name`} className="text-sm font-medium">
                {t("name")}
              </label>
              <input
                id={`${kind}-name`}
                className={CONTROL}
                value={name}
                onChange={(event) => setName(event.target.value)}
              />
              <p className="text-xs text-muted-foreground">{t("nameHint")}</p>
            </div>

            {hierarchical ? (
              <div className="flex flex-col gap-1.5">
                <label htmlFor={`${kind}-parent`} className="text-sm font-medium">
                  {t("parent")}
                </label>
                <select
                  id={`${kind}-parent`}
                  className={CONTROL}
                  value={parentId}
                  onChange={(event) => setParentId(event.target.value)}
                >
                  {/* A top-level category is the ordinary case, so the blank reads as an answer
                      rather than a prompt. */}
                  <option value="">{t("noParent")}</option>
                  {parentOptions.map((entry) => (
                    <option key={entry.id} value={entry.id}>
                      {entry.path}
                    </option>
                  ))}
                </select>
                <p className="text-xs text-muted-foreground">{t("parentHint")}</p>
              </div>
            ) : null}
          </div>

          <div className="flex gap-2">
            <Button type="submit" size="sm" disabled={save.isPending}>
              {save.isPending ? t("saving") : t("save")}
            </Button>
            <Button type="button" size="sm" variant="outline" onClick={() => setEditing(null)}>
              {t("cancel")}
            </Button>
          </div>
        </form>
      ) : null}

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-xl bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      {rows.length === 0 ? (
        <p className="text-sm text-muted-foreground">{t(`${kind}.empty`)}</p>
      ) : (
        <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
          {rows.map((entry) => (
            <li key={entry.id} className="flex flex-wrap items-center gap-3 px-4 py-2 text-sm">
              {/* Ancestry rather than the bare name, for the same reason the product form shows it:
                  a tree routinely repeats a leaf name under two parents. */}
              <span className="font-medium">
                {hierarchical ? (pathOf.get(entry.id) ?? entry.name) : entry.name}
              </span>

              <div className="ml-auto flex gap-2">
                {/* A tax class is what kind of thing a product is; the rate is what that kind costs
                    in one country at one time, and it changes when a government changes it. Two
                    lifetimes, so two screens — and without this one `PRD-07` can be classified and
                    never priced. Gated on read, like the screen it sits on; the editor gates its
                    own writes. */}
                {kind === "tax-classes" ? (
                  <LinkButton
                    href={`/products/classification/tax-classes/${entry.id}/rates`}
                    size="sm"
                    variant="outline"
                    aria-label={t("ratesNamed", { name: entry.name })}
                  >
                    <Percent className="size-4" />
                    {t("rates")}
                  </LinkButton>
                ) : null}

                {canWrite ? (
                  <>
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    onClick={() => open(entry)}
                    aria-label={t("editNamed", { name: entry.name })}
                  >
                    {t("edit")}
                  </Button>
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    disabled={remove.isPending}
                    onClick={() => remove.mutate(entry)}
                    aria-label={t("deleteNamed", { name: entry.name })}
                  >
                    {t("delete")}
                  </Button>
                  </>
                ) : null}
              </div>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/** Loads one vocabulary and hands it to the browser. */
export function Vocabulary({
  kind,
  queryKey,
  fetcher,
  hierarchical,
}: {
  kind: VocabularyKind;
  queryKey: readonly unknown[];
  fetcher: (accessToken: string, signal?: AbortSignal) => Promise<Vocabulary[] | Category[]>;
  hierarchical?: boolean;
}) {
  const t = useTranslations("Classification");
  const { user } = useAuth();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;

  const query = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey,
    queryFn: ({ signal }) => fetcher(accessToken!, signal),
  });

  if (query.isPending) {
    return <p className="text-sm text-muted-foreground">{t("loading")}</p>;
  }

  if (query.isError) {
    // Hidden rather than shown empty: a section that cannot load is not a section with nothing in
    // it, and offering its "New" button would be a control that cannot work.
    return (
      <p role="alert" className="text-sm text-destructive">
        {query.error instanceof ApiError && query.error.status === 403
          ? t("forbidden")
          : t("failed")}
      </p>
    );
  }

  return <VocabularyBrowser kind={kind} entries={query.data} hierarchical={hierarchical} />;
}
