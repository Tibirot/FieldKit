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
} from "@/lib/api/outlet-import";
import { parseCsv, withCell, writeCsv, type Csv } from "@/lib/csv";
import { cn } from "@/lib/utils";

const MODES: readonly OutletImportMode[] = ["AllOrNothing", "Partial"];

/**
 * A file the browser has read, held in memory for as long as this screen is open.
 *
 * Both the bytes and the parse. `text` is what was uploaded, untouched, and is what gets sent until
 * someone edits a cell — "check the file I gave you" should mean the file they gave us. From the
 * first edit onwards the parsed copy is the truth and is written back out.
 */
type Chosen = { name: string; text: string; csv: Csv; edited: boolean };

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
 * has open. It is also what the editable grid needs next: correcting a cell means re-serialising the
 * file that is already here and checking it again.
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
  const [mode, setMode] = useState<OutletImportMode>("AllOrNothing");
  const [result, setResult] = useState<OutletImportResult | null>(null);
  const [refused, setRefused] = useState<readonly string[]>([]);

  const maxRows = capabilities.data?.maxRows;
  const rows = chosen?.csv.rows.length;
  const tooBig = maxRows !== undefined && rows !== undefined && rows > maxRows;

  /**
   * Whether this browser read the file the same way the server did.
   *
   * Two CSV readers, one in C# and one here, and the grid depends on them agreeing about which row
   * is row 7. If they do not, every flag would land on the wrong shop — so the counts are compared
   * and the grid is simply not offered when they disagree. The rejected-rows download is the answer
   * then, which is what it is for.
   */
  const aligned = result !== null && rows === result.totalRows;

  const run = useMutation({
    mutationFn: (dryRun: boolean) =>
      importOutlets(
        accessToken!,
        {
          text: chosen!.edited ? writeCsv(chosen!.csv) : chosen!.text,
          mediaType: capabilities.data!.mediaTypes[0],
        },
        { mode, dryRun },
      ),

    onSuccess: async (outcome) => {
      setRefused([]);
      setResult(outcome);

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

  async function choose(file: File | undefined) {
    setResult(null);
    setRefused([]);

    if (!file) {
      setChosen(null);
      return;
    }

    const text = await file.text();

    setChosen({ name: file.name, text, csv: parseCsv(text), edited: false });
  }

  function edit(row: number, column: number, value: string) {
    setChosen((current) =>
      current === null
        ? null
        : { ...current, csv: withCell(current.csv, row, column, value), edited: true },
    );
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

        <div className="flex flex-wrap gap-2">
          <Button
            type="button"
            disabled={!chosen || tooBig || run.isPending}
            onClick={() => run.mutate(true)}
          >
            {run.isPending ? t("checking") : t("check")}
          </Button>

          <Button
            type="button"
            variant="outline"
            // Only after a dry run, and only one that found something to write. "Apply" on a file
            // nobody has checked is the mode this endpoint deliberately does not have.
            disabled={!result?.dryRun || result.accepted === 0 || run.isPending}
            onClick={() => run.mutate(false)}
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

      {result ? <Outcome result={result} fileName={chosen?.name ?? "outlets.csv"} /> : null}

      {result && chosen && aligned && result.problems.length > 0 ? (
        <OutletImportGrid
          file={chosen.csv}
          problems={result.problems}
          onEdit={edit}
          onRecheck={() => run.mutate(true)}
          busy={run.isPending}
        />
      ) : null}
    </div>
  );
}

function Outcome({ result, fileName }: { result: OutletImportResult; fileName: string }) {
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
