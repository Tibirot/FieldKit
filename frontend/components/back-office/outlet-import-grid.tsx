"use client";

import { useTranslations } from "next-intl";

import { Button } from "@/components/ui/button";
import type { OutletImportProblem, OutletImportRow } from "@/lib/api/outlet-import";
import { cn } from "@/lib/utils";

/**
 * How many rows the grid will render.
 *
 * A cap rather than a scroll: 4,000 refused rows is 24,000 inputs, and a browser asked to build them
 * stops being a browser. Past this the rejected-rows download is the honest answer — the spec keeps
 * it precisely as the escape hatch for files too big to review by eye — and the screen says so
 * rather than showing the first hundred as if that were all of them.
 */
const MaxRows = 100;

/** Everything wrong with one row, by the column it is about. */
type RowProblems = {
  columns: Map<string, string[]>;
  whole: string[];
};

/**
 * The refused rows, corrected in place (`OUT-05`).
 *
 * **Before the write rather than after it.** A grid that appeared once the import had run would be
 * editing outlets, which is the Outlets screen's job — and the difference is between an admin fixing
 * a typo and an admin fixing a typo that is now a shop other people can already see.
 *
 * **The rows are the server's, not a second reading of the upload.** They arrive on the dry run
 * alongside the problems, numbered by the same reader that numbered those — so this component never
 * decides which row is row 7, and cannot flag a cell in the wrong shop. Writing the corrected file
 * back out is the only CSV work left here, and a writer emits what it is given.
 *
 * Only the refused rows are shown. The rest are correct, and 3,988 of them scrolling past is a way
 * of hiding the twelve that need attention.
 */
export function OutletImportGrid({
  columns,
  rows,
  problems,
  onEdit,
  onRecheck,
  busy,
}: {
  columns: string[];
  rows: OutletImportRow[];
  problems: OutletImportProblem[];
  onEdit: (row: number, column: number, value: string) => void;
  onRecheck: () => void;
  busy: boolean;
}) {
  const t = useTranslations("OutletImport");

  const byRow = group(problems);
  const refused = rows.filter((row) => byRow.has(row.row));
  const shown = refused.slice(0, MaxRows);

  if (shown.length === 0) return null;

  return (
    <section className="flex flex-col gap-3">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <h2 className="text-sm font-semibold">{t("fixTitle")}</h2>
        <p className="text-xs text-muted-foreground">{t("fixHint")}</p>
      </div>

      <div className="overflow-x-auto rounded-xl border border-border">
        <table className="w-full text-sm">
          <caption className="sr-only">{t("fixCaption")}</caption>
          <thead className="bg-muted/50 text-xs uppercase">
            <tr>
              <th scope="col" className="px-3 py-2 text-left font-medium">
                {t("row")}
              </th>
              {columns.map((column) => (
                <th key={column} scope="col" className="px-3 py-2 text-left font-mono font-medium">
                  {column}
                </th>
              ))}
              <th scope="col" className="px-3 py-2 text-left font-medium">
                {t("problem")}
              </th>
            </tr>
          </thead>
          <tbody>
            {shown.map(({ row: number, values }) => {
              const row = byRow.get(number)!;
              const problemId = `row-${number}-problems`;

              return (
                <tr key={number} className="border-t border-border align-top">
                  <th scope="row" className="px-3 py-2 text-left font-normal tabular-nums">
                    {number}
                  </th>

                  {columns.map((column, at) => {
                    const flagged = row.columns.has(column);

                    const value = values[at] ?? "";

                    // A textarea only where one is needed. `<input value>` is sanitised by the DOM —
                    // it drops newlines — so a quoted cell holding an address over two lines would
                    // display as one, and flatten for real the moment someone typed in it. Found by
                    // uploading such a file; a jsdom test would have reported the value it set.
                    const Control = value.includes("\n") ? "textarea" : "input";

                    return (
                      <td key={column} className="px-2 py-1.5">
                        <Control
                          value={value}
                          rows={Control === "textarea" ? 2 : undefined}
                          onChange={(event) => onEdit(number, at, event.target.value)}
                          // Named for the cell, because a grid of inputs is otherwise a grid of
                          // unlabelled boxes to anything that is not looking at it.
                          aria-label={t("cellLabel", { column, row: number })}
                          aria-invalid={flagged}
                          aria-describedby={flagged ? problemId : undefined}
                          className={cn(
                            "w-full min-w-28 rounded-lg border bg-background px-2 py-1 text-sm",
                            "focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none",
                            Control === "input" && "h-8",
                            flagged ? "border-destructive" : "border-input",
                          )}
                        />
                      </td>
                    );
                  })}

                  <td id={problemId} className="px-3 py-2 text-xs text-destructive">
                    <ul className="flex flex-col gap-1">
                      {[...row.columns.values(), row.whole].flat().map((message) => (
                        <li key={message}>{message}</li>
                      ))}
                    </ul>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      {refused.length > shown.length ? (
        <p className="text-xs text-muted-foreground">
          {t("tooManyToFix", { shown: shown.length, total: refused.length })}
        </p>
      ) : null}

      <div>
        <Button type="button" onClick={onRecheck} disabled={busy}>
          {busy ? t("checking") : t("recheck")}
        </Button>
      </div>
    </section>
  );
}

/**
 * The problems, by the row they are about.
 *
 * Grouped by column as well, because a cell can be wrong twice and the grid flags the control once
 * while listing both reasons. `column: null` is about the row as a whole — "this row has no code" —
 * and belongs beside the row rather than pinned to a box at random.
 */
function group(problems: OutletImportProblem[]): Map<number, RowProblems> {
  const byRow = new Map<number, RowProblems>();

  for (const problem of problems) {
    const row: RowProblems = byRow.get(problem.row) ?? { columns: new Map(), whole: [] };

    if (problem.column === null) {
      row.whole.push(problem.message);
    } else {
      row.columns.set(problem.column, [...(row.columns.get(problem.column) ?? []), problem.message]);
    }

    byRow.set(problem.row, row);
  }

  return byRow;
}
