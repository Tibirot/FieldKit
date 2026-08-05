"use client";

import { useTranslations } from "next-intl";
import { useState } from "react";

import { Button } from "@/components/ui/button";
import type { OutletImportProblem, OutletImportRow } from "@/lib/api/outlet-import";
import { cn } from "@/lib/utils";

/**
 * How many rows the grid will render at once.
 *
 * A cap rather than a scroll: 4,000 rows across eight columns is 32,000 inputs, and a browser asked
 * to build them stops being a browser. The screen says what it is not showing rather than presenting
 * the first hundred as if that were the file — and `rejectedRowsCsv` stays the answer at that size,
 * which is what the spec keeps it for.
 */
const MaxRows = 100;

/** Everything wrong with one row, by the column it is about. */
type RowProblems = {
  columns: Map<string, string[]>;
  whole: string[];
};

/**
 * The uploaded file, corrected in place (`OUT-05`).
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
 * **The whole file, not just the refused rows.** Showing only what failed hides the two things the
 * good rows are evidence of: that the columns mapped the way the admin expected, and that a problem
 * naming another row (a code duplicated on row 3) can be read against that row. The filter above the
 * table narrows it to the rows that need attention, which is the default when there are any.
 */
export function OutletImportGrid({
  columns,
  rows,
  problems,
  excluded,
  onEdit,
  onToggle,
  onToggleAll,
  onRecheck,
  busy,
}: {
  columns: string[];
  rows: OutletImportRow[];
  problems: OutletImportProblem[];
  /** Rows the admin has unchecked, by their file row number. */
  excluded: ReadonlySet<number>;
  onEdit: (row: number, column: number, value: string) => void;
  onToggle: (row: number, include: boolean) => void;
  onToggleAll: (include: boolean) => void;
  onRecheck: () => void;
  busy: boolean;
}) {
  const t = useTranslations("OutletImport");

  const byRow = group(problems);
  const [onlyProblems, setOnlyProblems] = useState(true);

  const filtered = onlyProblems && byRow.size > 0 ? rows.filter((row) => byRow.has(row.row)) : rows;
  const shown = filtered.slice(0, MaxRows);

  if (rows.length === 0) return null;

  return (
    <section className="flex flex-col gap-3">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <h2 className="text-sm font-semibold">{t("fixTitle")}</h2>
        <p className="text-xs text-muted-foreground">{t("fixHint")}</p>
      </div>

      {byRow.size > 0 ? (
        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={onlyProblems}
            onChange={(event) => setOnlyProblems(event.target.checked)}
            className="size-4 accent-primary"
          />
          {t("onlyProblems")}
        </label>
      ) : null}

      <div className="overflow-x-auto rounded-xl border border-border">
        <table className="w-full text-sm">
          <caption className="sr-only">{t("fixCaption")}</caption>
          <thead className="bg-muted/50 text-xs uppercase">
            <tr>
              <th scope="col" className="px-3 py-2 text-left font-medium">
                <input
                  type="checkbox"
                  // Indeterminate is the honest state for a partial selection, and it is a property
                  // rather than an attribute — React will not set it from JSX.
                  ref={(box) => {
                    if (box) box.indeterminate = excluded.size > 0 && excluded.size < rows.length;
                  }}
                  checked={excluded.size === 0}
                  onChange={(event) => onToggleAll(event.target.checked)}
                  aria-label={t("includeAll")}
                  className="size-4 accent-primary"
                />
              </th>
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
              const row = byRow.get(number);
              const problemId = `row-${number}-problems`;
              const skipped = excluded.has(number);

              return (
                <tr
                  key={number}
                  className={cn("border-t border-border align-top", skipped && "opacity-50")}
                >
                  <td className="px-3 py-2">
                    <input
                      type="checkbox"
                      checked={!skipped}
                      onChange={(event) => onToggle(number, event.target.checked)}
                      aria-label={t("includeRow", { row: number })}
                      className="size-4 accent-primary"
                    />
                  </td>

                  <th scope="row" className="px-3 py-2 text-left font-normal tabular-nums">
                    {number}
                  </th>

                  {columns.map((column, at) => {
                    const flagged = row?.columns.has(column) ?? false;
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
                    {row ? (
                      <ul className="flex flex-col gap-1">
                        {[...row.columns.values(), row.whole].flat().map((message) => (
                          <li key={message}>{message}</li>
                        ))}
                      </ul>
                    ) : null}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      {filtered.length > shown.length ? (
        <p className="text-xs text-muted-foreground">
          {t("tooManyToFix", { shown: shown.length, total: filtered.length })}
        </p>
      ) : null}

      <div>
        <Button
          type="button"
          onClick={onRecheck}
          // Unchecking every row would send a header and nothing else, which the import refuses as a
          // file with no rows — wiping the grid, and the corrections in it, to say so.
          disabled={busy || excluded.size === rows.length}
        >
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
