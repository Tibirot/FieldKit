/**
 * A CSV file, as rows of cells.
 *
 * `rows` holds data rows only — the header is `columns`. Every row is padded to the header's width,
 * so `rows[i][c]` is always the cell under `columns[c]` and a short row cannot silently shift the
 * columns after it.
 */
export type Csv = {
  columns: string[];
  rows: string[][];
};

/**
 * The file row number the server will report for data row `index`.
 *
 * The header is row 1 and rows are counted as **records**, not physical lines — a quoted field
 * containing a newline is one row. That is what the server counts (`csv.Parser.Row`) and what a
 * spreadsheet shows, which is the whole point: the number in a problem has to be one the admin can
 * navigate to.
 */
export const fileRow = (index: number) => index + 2;

/**
 * Reads CSV text the way the import does.
 *
 * Hand-rolled, and small enough to be worth reading before trusting: a field may contain the
 * delimiter, a newline, and doubled quotes, and the split-on-comma version of this works on every
 * file until it meets a store called `Smith, Jones & Co`.
 *
 * **It only has to agree with the server, not with every CSV dialect.** Blank lines are skipped and
 * rows are counted as records, because that is what `CsvOutletImportReader` does. Where they might
 * still disagree — a file this reads as a different number of rows than the server did — the screen
 * checks the two counts before offering to edit anything, so a disagreement becomes a visible
 * fallback rather than an admin correcting the wrong row.
 */
export function parseCsv(text: string): Csv {
  const records = read(text);

  if (records.length === 0) return { columns: [], rows: [] };

  const columns = records[0].map((header) => header.trim());
  const rows = records.slice(1).map((record) => pad(record, columns.length));

  return { columns, rows };
}

/**
 * Writes it back out.
 *
 * A cell is quoted when it has to be — a delimiter, a quote, or a newline in it — and left alone
 * otherwise, so a file that never needed quoting comes back looking like the one that was uploaded.
 * `\r\n` because that is what a spreadsheet writes and what this is going back into.
 */
export function writeCsv(csv: Csv): string {
  return [csv.columns, ...csv.rows].map((record) => record.map(quote).join(",")).join("\r\n") + "\r\n";
}

/** Replaces one cell, leaving every other row identical. */
export function withCell(csv: Csv, row: number, column: number, value: string): Csv {
  return {
    columns: csv.columns,
    rows: csv.rows.map((cells, index) =>
      index === row ? cells.map((cell, at) => (at === column ? value : cell)) : cells,
    ),
  };
}

function quote(cell: string): string {
  return /[",\r\n]/.test(cell) ? `"${cell.replaceAll('"', '""')}"` : cell;
}

/** Pads or trims a record to the header's width — see the note on {@link Csv}. */
function pad(record: string[], width: number): string[] {
  const cells = record.slice(0, width);

  while (cells.length < width) cells.push("");

  return cells;
}

/**
 * Splits the text into records.
 *
 * One pass, character by character. `inQuotes` is the whole state machine: inside a quoted field a
 * comma and a newline are data, and a doubled quote is one quote.
 */
function read(text: string): string[][] {
  const records: string[][] = [];

  let record: string[] = [];
  let cell = "";
  let inQuotes = false;
  let started = false;

  const endCell = () => {
    record.push(cell);
    cell = "";
    started = false;
  };

  const endRecord = () => {
    endCell();

    // A trailing blank line is what a text editor leaves behind, not a row an admin meant — the
    // same forgiveness the server extends (`IgnoreBlankLines`), and it has to match or the row
    // numbers drift by one from the first empty line onwards.
    if (record.some((value) => value.trim() !== "")) records.push(record);

    record = [];
  };

  for (let at = 0; at < text.length; at++) {
    const character = text[at];

    if (inQuotes) {
      if (character !== '"') {
        cell += character;
      } else if (text[at + 1] === '"') {
        cell += '"';
        at++;
      } else {
        inQuotes = false;
      }

      continue;
    }

    // Only opens a quoted field at the start of a cell. A quote in the middle of one is a character
    // someone typed, not a delimiter — `12" shelf` should survive.
    if (character === '"' && !started) {
      inQuotes = true;
      started = true;
    } else if (character === ",") {
      endCell();
    } else if (character === "\n") {
      endRecord();
    } else if (character !== "\r") {
      cell += character;
      started = true;
    }
  }

  // Whatever the last line left behind, when the file does not end with a newline.
  if (cell !== "" || record.length > 0) endRecord();

  return records;
}
