"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { OutletImportGrid } from "@/components/back-office/outlet-import-grid";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import {
  fetchImportCapabilities,
  importCapabilitiesKey,
  importOutlets,
  type OutletImportMode,
  type OutletImportResult,
  type OutletImportRow,
} from "@/lib/api/outlet-import";
import { roughRowCount, writeCsv } from "@/lib/csv";
import { cn } from "@/lib/utils";

const MODES: readonly OutletImportMode[] = ["AllOrNothing", "Partial"];

/** A file the browser has read, held for as long as this screen is open. */
type Chosen = { name: string; text: string };

/**
 * The file as the server read it, and what has been corrected in it.
 *
 * The rows come from the response, never from parsing the upload here — see `OutletImportRow`. So
 * this screen holds no opinion about which row is row 7, which is the one thing it could get wrong
 * in a way nobody would notice.
 */
type Corrections = { columns: string[]; rows: OutletImportRow[] };

/**
 * Bulk import of the outlet base (`OUT-05`).
 *
 * **Check, then apply.** The dry run executes every rule and writes nothing, and returns exactly
 * what the real run would — so what is on screen before someone commits is the answer and not a
 * preview to be trusted. That is the whole reason `dryRun` costs nothing.
 *
 * The file stays in the browser between the two calls. It is sent twice rather than parked on the
 * server behind a token, because a synchronous import has no result to outlive its response, and
 * keeping one would mean a table, a retention rule and a cleanup job for a file the admin already
 * has open. It is also what makes the grid possible: correcting a cell means writing the file back
 * out from rows the server itself read, and checking it again.
 */
export function OutletImport() {
  const t = useTranslations("OutletImport");
  const { user } = useAuth();
  const client = useQueryClient();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;

  const capabilities = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey: importCapabilitiesKey(subject ?? ""),
    queryFn: ({ signal }) => fetchImportCapabilities(accessToken!, signal),
  });

  const [chosen, setChosen] = useState<Chosen | null>(null);
  const [file, setFile] = useState<Corrections | null>(null);

  /** Rows the admin has unchecked, by file row number. Emptied by a check, which drops them. */
  const [excluded, setExcluded] = useState<ReadonlySet<number>>(new Set());

  /**
   * Whether the uploaded bytes are still what this screen would send.
   *
   * Sticky until a different file is picked, and deliberately not cleared by a dry run. It was, and
   * that made Apply send the original upload again — because the run that proved the corrections
   * good was also the run that forgot about them. Nothing on screen said so: the check reported
   * three rows ready and the apply imported none.
   */
  const [pristine, setPristine] = useState(true);

  /**
   * Whether the file has changed since the last check.
   *
   * What makes "check, then apply" exact rather than nearly true: an edit or an exclusion after a
   * check means the result on screen describes a file that no longer exists, so Apply is unavailable
   * until it has been checked again. Without it, correcting a cell and pressing Apply writes
   * something nobody has looked at.
   */
  const [dirty, setDirty] = useState(false);
  const [mode, setMode] = useState<OutletImportMode>("AllOrNothing");
  const [result, setResult] = useState<OutletImportResult | null>(null);
  const [refused, setRefused] = useState<readonly string[]>([]);

  /** How many rows the run behind `result` left out — the admin's own exclusions, not the server's. */
  const [skipped, setSkipped] = useState(0);

  const everyRowExcluded = file !== null && excluded.size === file.rows.length && file.rows.length > 0;

  const maxRows = capabilities.data?.maxRows;
  const rows = chosen === undefined ? undefined : chosen && roughRowCount(chosen.text);
  const tooBig = maxRows !== undefined && rows != null && rows > maxRows;

  const run = useMutation({
    mutationFn: ({ dryRun }: { dryRun: boolean; skipped: number }) =>
      importOutlets(
        accessToken!,
        {
          // The uploaded bytes until something is changed: "check the file I gave you" should mean
          // the file they gave us. After that it is the server's own rows, minus the ones unchecked,
          // written back out.
          text: pristine || !file
            ? chosen!.text
            : writeCsv(
                file.columns,
                file.rows.filter((row) => !excluded.has(row.row)).map((row) => row.values),
              ),
          mediaType: capabilities.data!.mediaTypes[0],
        },
        { mode, dryRun },
      ),

    onSuccess: async (outcome, sent) => {
      setRefused([]);
      setResult(outcome);
      setSkipped(sent.skipped);
      setDirty(false);

      // A dry run re-reads whatever was sent, so its rows supersede the ones being corrected — a
      // correction that landed is now simply part of the file, and an unchecked row is no longer in
      // it at all. A real run sends none, and there is nothing left to correct anyway.
      if (outcome.dryRun) {
        setFile({ columns: outcome.columns, rows: outcome.rows });
        setExcluded(new Set());
      }

      // Only a real run changed anything. Invalidating after a dry run would refetch every list to
      // find them exactly as they were.
      if (outcome.imported > 0) await client.invalidateQueries({ queryKey: ["outlets"] });
    },

    onError: (error) => {
      setResult(null);
      setRefused(
        error instanceof ApiError && error.problems.length > 0
          ? error.problems.map((problem) => problem.message)
          : [t("failed")],
      );
    },
  });

  async function choose(picked: File | undefined) {
    setResult(null);
    setRefused([]);
    setFile(null);
    setExcluded(new Set());
    setPristine(true);
    setDirty(false);

    setChosen(picked ? { name: picked.name, text: await picked.text() } : null);
  }

  /** Any change to the file: it is no longer the upload, and no longer what was checked. */
  function changed() {
    setPristine(false);
    setDirty(true);
  }

  function edit(row: number, column: number, value: string) {
    changed();

    setFile((current) =>
      current === null
        ? null
        : {
            ...current,
            rows: current.rows.map((candidate) =>
              candidate.row === row
                ? { row, values: candidate.values.map((cell, at) => (at === column ? value : cell)) }
                : candidate,
            ),
          },
    );
  }

  function toggle(row: number, include: boolean) {
    changed();

    setExcluded((current) => {
      const next = new Set(current);

      if (include) next.delete(row);
      else next.add(row);

      return next;
    });
  }

  function toggleAll(include: boolean) {
    changed();
    setExcluded(include ? new Set() : new Set(file?.rows.map((row) => row.row) ?? []));
  }

  function check() {
    run.mutate({ dryRun: true, skipped: excluded.size });
  }

  return (
    <div className="flex flex-col gap-4">
      <section className="flex flex-col gap-4 rounded-xl border border-border p-4">
        <div className="flex flex-col gap-1.5">
          <label htmlFor="file" className="text-sm font-medium">
            {t("file")}
          </label>
          <input
            id="file"
            type="file"
            // From the server's own answer rather than a hard-coded ".csv": the day JSON and Excel
            // land, this widens without a second commit.
            accept={capabilities.data?.mediaTypes.join(",")}
            onChange={(event) => choose(event.target.files?.[0])}
            className="text-sm file:mr-3 file:rounded-lg file:border file:border-input file:bg-background file:px-3 file:py-1.5 file:text-sm"
          />
          {maxRows !== undefined ? (
            <p className="text-xs text-muted-foreground">{t("atMost", { max: maxRows })}</p>
          ) : null}
        </div>

        <fieldset className="flex flex-col gap-2">
          <legend className="text-sm font-medium">{t("mode")}</legend>
          {MODES.map((option) => (
            <label key={option} className="flex items-start gap-2 text-sm">
              <input
                type="radio"
                name="mode"
                value={option}
                checked={mode === option}
                onChange={() => setMode(option)}
                className="mt-1 accent-primary"
              />
              <span>
                <span className="font-medium">{t(`mode${option}`)}</span>
                <span className="block text-xs text-muted-foreground">
                  {t(`mode${option}Hint`)}
                </span>
              </span>
            </label>
          ))}
        </fieldset>

        {tooBig ? (
          // Refused here rather than by uploading twelve megabytes to be told the same thing. The
          // cap comes from the server, so this cannot disagree with what would actually happen.
          <p role="alert" className="text-sm text-destructive">
            {t("tooBig", { rows: rows!, max: maxRows })}
          </p>
        ) : null}

        {excluded.size > 0 ? (
          <p className="text-sm">{t("excluded", { count: excluded.size })}</p>
        ) : null}

        {dirty && result ? (
          // The result on screen describes a file that no longer exists. Saying so is better than
          // quietly leaving Apply available and writing something nobody has looked at.
          <p className="text-sm text-muted-foreground">{t("stale")}</p>
        ) : null}

        <div className="flex flex-wrap gap-2">
          <Button
            type="button"
            disabled={!chosen || tooBig || run.isPending || everyRowExcluded}
            onClick={() => check()}
          >
            {run.isPending ? t("checking") : t("check")}
          </Button>

          <Button
            type="button"
            variant="outline"
            // Only after a dry run, only one that found something to write, and only while the file
            // is still the one that run described. "Apply" on a file nobody has checked is the mode
            // this endpoint deliberately does not have.
            disabled={!result?.dryRun || result.accepted === 0 || dirty || run.isPending}
            onClick={() => run.mutate({ dryRun: false, skipped: excluded.size })}
          >
            {t("apply")}
          </Button>
        </div>
      </section>

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-xl bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      {result ? (
        <Outcome result={result} skipped={skipped} fileName={chosen?.name ?? "outlets.csv"} />
      ) : null}

      {/*
        Kept on screen through a failed run: the corrections in it are the admin's work, and losing
        them to a network error would be worse than showing a grid whose problems are one request
        out of date.
      */}
      {file ? (
        <OutletImportGrid
          columns={file.columns}
          rows={file.rows}
          problems={result?.problems ?? []}
          excluded={excluded}
          onEdit={edit}
          onToggle={toggle}
          onToggleAll={toggleAll}
          onRecheck={check}
          busy={run.isPending}
        />
      ) : null}
    </div>
  );
}

function Outcome({
  result,
  skipped,
  fileName,
}: {
  result: OutletImportResult;
  skipped: number;
  fileName: string;
}) {
  const t = useTranslations("OutletImport");

  return (
    <section className="flex flex-col gap-4">
      <p
        className={cn(
          "rounded-xl px-4 py-3 text-sm",
          result.rejected === 0 ? "bg-primary/10 text-foreground" : "bg-destructive/10",
        )}
      >
        {/*
          Three numbers because there are three different sentences to say: what is valid, what is
          wrong, and what is now in the database. After a dry run — or an AllOrNothing run that hit
          one bad row — `imported` is 0 while `accepted` is not, and collapsing them would make the
          screen claim an import that did not happen.
        */}
        {result.dryRun
          ? t("checked", { accepted: result.accepted, rejected: result.rejected })
          : t("applied", { imported: result.imported, rejected: result.rejected })}
      </p>

      {skipped > 0 ? (
        // Stated rather than left to be inferred from the arithmetic: a file of 40 rows reporting 37
        // accepted is a question, and "3 were excluded" is the answer to it.
        <p className="text-sm text-muted-foreground">{t("wereExcluded", { count: skipped })}</p>
      ) : null}

      {!result.dryRun && result.imported === 0 && result.accepted > 0 ? (
        <p className="text-sm text-muted-foreground">{t("nothingWritten")}</p>
      ) : null}

      {result.ignoredColumns.length > 0 ? (
        // A real export is full of `legacy_id`, so refusing the file would be hostile — but a
        // mistyped custom-field header looks identical, and passing it over in silence is how a
        // column of data goes missing without anyone noticing.
        <p className="rounded-xl bg-muted px-4 py-3 text-sm">
          {t("ignoredColumns", { columns: result.ignoredColumns.join(", ") })}
        </p>
      ) : null}

      {result.problems.length > 0 ? (
        <div className="overflow-x-auto rounded-xl border border-border">
          <table className="w-full text-sm">
            <caption className="sr-only">{t("problemsCaption")}</caption>
            <thead className="bg-muted/50 text-xs uppercase">
              <tr>
                <th scope="col" className="px-3 py-2 text-left font-medium">
                  {t("row")}
                </th>
                <th scope="col" className="px-3 py-2 text-left font-medium">
                  {t("column")}
                </th>
                <th scope="col" className="px-3 py-2 text-left font-medium">
                  {t("problem")}
                </th>
              </tr>
            </thead>
            <tbody>
              {result.problems.map((problem) => (
                <tr key={`${problem.row}-${problem.column}-${problem.message}`} className="border-t border-border">
                  {/* The line number in the uploaded file, header included — what their editor shows. */}
                  <td className="px-3 py-2 tabular-nums">{problem.row}</td>
                  <td className="px-3 py-2 font-mono text-xs">{problem.column ?? "—"}</td>
                  <td className="px-3 py-2">{problem.message}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}

      {result.rejectedRowsCsv ? (
        // The escape hatch, and the thing that stops a partial import being a trap: re-sending the
        // original would now collide with everything that landed, so the refused rows come back in
        // the shape they were sent. Fix that file, send that file.
        <div>
          <Button
            type="button"
            variant="outline"
            onClick={() => download(`rejected-${fileName}`, result.rejectedRowsCsv!)}
          >
            {t("downloadRejected")}
          </Button>
        </div>
      ) : null}
    </section>
  );
}

/** Hands the browser a file it already has, without a round trip to ask for it back. */
function download(name: string, csv: string) {
  const url = URL.createObjectURL(new Blob([csv], { type: "text/csv;charset=utf-8" }));
  const link = document.createElement("a");

  link.href = url;
  link.download = name;
  link.click();

  URL.revokeObjectURL(url);
}
