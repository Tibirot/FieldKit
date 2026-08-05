import { ApiError, type FieldProblem } from "@/lib/api/client";

/**
 * What the import does when some rows are bad (`OUT-05`).
 *
 * The admin's choice, because both answers are right for different files: a 40-row list someone
 * typed should be fixed and re-sent whole, while a 4,000-row export from a system that has been
 * accumulating dirt for a decade will never be clean, and refusing all of it means refusing the
 * migration.
 */
export type OutletImportMode = "AllOrNothing" | "Partial";

/** Something wrong with one row, in terms of the file that was uploaded. */
export type OutletImportProblem = {
  /** The line number in the file, header included — what the admin's spreadsheet shows. */
  row: number;
  /** The column, when the problem is about one. Null when the whole row is the problem. */
  column: string | null;
  message: string;
};

/**
 * One row of the file, as the import read it.
 *
 * **So a screen can correct a row without parsing the file itself.** A client that re-read the
 * upload would be a second CSV reader, and the two only have to disagree about which row is row 7
 * for every flagged cell to land on the wrong shop — a failure with no symptom until someone
 * corrects data that was fine. The reader that numbered the problems is the one that says what is in
 * the row.
 */
export type OutletImportRow = {
  /** The file's own row number, matching `OutletImportProblem.row`. */
  row: number;
  /** Aligned to `OutletImportResult.columns`. A blank cell is an empty string. */
  values: string[];
};

export type OutletImportResult = {
  totalRows: number;
  /** Rows that passed every rule. */
  accepted: number;
  rejected: number;
  /** What is actually in the database now — 0 after a dry run, or after a failed AllOrNothing run. */
  imported: number;
  dryRun: boolean;
  mode: OutletImportMode;
  problems: OutletImportProblem[];
  /** The refused rows in the shape they arrived, plus a reason column. Null when nothing failed. */
  rejectedRowsCsv: string | null;
  ignoredColumns: string[];

  /** The file's own columns, in its own order. Empty outside a dry run. */
  columns: string[];

  /** The file as the import read it — on a dry run only, since a real run has nothing to correct. */
  rows: OutletImportRow[];
};

/**
 * What the import accepts, asked before a file is sent.
 *
 * From the server rather than declared here. The row cap is a rule only the server enforces, and a
 * copy of it in this file would drift without anything failing — the screen would simply start
 * lying about the limit.
 */
export type OutletImportCapabilities = {
  maxRows: number;
  mediaTypes: string[];
  reasonColumn: string;
};

export const importCapabilitiesKey = (subject: string) => ["outlet-import", subject] as const;

export function fetchImportCapabilities(
  accessToken: string,
  signal?: AbortSignal,
): Promise<OutletImportCapabilities> {
  return request<OutletImportCapabilities>("/api/outlets/import", {
    headers: { Authorization: `Bearer ${accessToken}`, Accept: "application/json" },
    signal,
  });
}

/**
 * Sends the file, and answers what happened to it.
 *
 * **The file is the body**, not a multipart part — there are no other parts. The mode and the
 * dry-run flag are query parameters because they are *how* to run the import rather than part of
 * what is being imported, which leaves `Content-Type` free to choose the reader.
 *
 * `dryRun` runs every rule and writes nothing, and it returns exactly what the real run would. That
 * is what makes "check, then apply" two calls with one answer between them rather than a preview
 * that has to be trusted.
 */
export function importOutlets(
  accessToken: string,
  file: { text: string; mediaType: string },
  options: { mode: OutletImportMode; dryRun: boolean },
  signal?: AbortSignal,
): Promise<OutletImportResult> {
  const query = new URLSearchParams({ mode: options.mode, dryRun: String(options.dryRun) });

  return request<OutletImportResult>(`/api/outlets/import?${query}`, {
    method: "POST",
    headers: {
      Authorization: `Bearer ${accessToken}`,
      Accept: "application/json",
      "Content-Type": file.mediaType,
    },
    body: file.text,
    signal,
  });
}

/**
 * One fetch, and a refusal that keeps what the server said.
 *
 * Not `apiGet`/`apiSend` from the shared client: those serialize a JSON body, and the whole point of
 * this endpoint is that the body is the file exactly as it was read. The refusal envelope is the
 * shared one (api-contracts §3), so that part is not re-invented.
 */
async function request<T>(path: string, init: RequestInit): Promise<T> {
  const response = await fetch(path, init);

  if (!response.ok) {
    let problems: FieldProblem[] = [];

    try {
      problems = ((await response.json()) as { errors?: FieldProblem[] }).errors ?? [];
    } catch {
      problems = [];
    }

    throw new ApiError(response.status, problems);
  }

  return (await response.json()) as T;
}
