/**
 * Writes rows back out as CSV.
 *
 * **There is no reader here, deliberately.** The import screen edits rows the *server* read and
 * numbered, so this file never has to decide which row is row 7. A second CSV reader would have to
 * agree with `CsvOutletImportReader` about quoted delimiters, embedded newlines, blank lines and
 * record counting — and where it did not, every flagged cell would land on the wrong shop with
 * nothing to say so. A writer cannot make that mistake: it emits the rows it was given, in order.
 *
 * A cell is quoted only when it has to be — a delimiter, a quote, or a newline in it — so a file
 * that never needed quoting comes back looking like the one that was uploaded. `\r\n` because that
 * is what a spreadsheet writes, and this is going straight back into one.
 */
export function writeCsv(columns: readonly string[], rows: readonly (readonly string[])[]): string {
  return [columns, ...rows].map((record) => record.map(quote).join(",")).join("\r\n") + "\r\n";
}

function quote(cell: string): string {
  return /[",\r\n]/.test(cell) ? `"${cell.replaceAll('"', '""')}"` : cell;
}

/**
 * Roughly how many data rows a file has, without reading it properly.
 *
 * Only ever used to refuse an oversized file *before* uploading twelve megabytes of it, and only
 * approximate: a quoted field containing a newline is one row that looks like two here. The server
 * counts properly and its answer is the one that decides — the cost of being wrong is a round trip,
 * not a wrong row.
 */
export function roughRowCount(text: string): number {
  const lines = text.split(/\r?\n/).filter((line) => line.trim() !== "");

  return Math.max(0, lines.length - 1);
}
