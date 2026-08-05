import { describe, expect, it } from "vitest";

import { fileRow, parseCsv, withCell, writeCsv } from "@/lib/csv";

describe("parseCsv", () => {
  it("reads a header and its rows", () => {
    const csv = parseCsv("code,name\nOUT-1,Corner Shop\nOUT-2,High Street\n");

    expect(csv.columns).toEqual(["code", "name"]);
    expect(csv.rows).toEqual([
      ["OUT-1", "Corner Shop"],
      ["OUT-2", "High Street"],
    ]);
  });

  it("keeps a delimiter that is inside a quoted field", () => {
    // The split-on-comma version of this parser works on every file until it meets a store called
    // "Smith, Jones & Co".
    const csv = parseCsv('code,name\nOUT-1,"Smith, Jones & Co"\n');

    expect(csv.rows).toEqual([["OUT-1", "Smith, Jones & Co"]]);
  });

  it("reads a doubled quote as one quote, and a bare one as a character", () => {
    const csv = parseCsv('code,name\nOUT-1,"He said ""hello"""\nOUT-2,12" shelf\n');

    expect(csv.rows).toEqual([
      ["OUT-1", 'He said "hello"'],
      ["OUT-2", '12" shelf'],
    ]);
  });

  it("treats a newline inside a quoted field as part of the value, not a new row", () => {
    // The reason rows are counted as records rather than lines. Getting this wrong shifts every
    // row number after it, which would have the screen flag a cell in the wrong shop.
    const csv = parseCsv('code,address\nOUT-1,"Str. Dorobanti 1\nBucharest"\nOUT-2,Elsewhere\n');

    expect(csv.rows).toEqual([
      ["OUT-1", "Str. Dorobanti 1\nBucharest"],
      ["OUT-2", "Elsewhere"],
    ]);
  });

  it("skips blank lines, the way the server does", () => {
    // A trailing blank line is what a text editor leaves behind. It has to be skipped on both sides
    // or the numbers drift by one from the first empty line onwards.
    const csv = parseCsv("code,name\nOUT-1,Corner Shop\n\n\nOUT-2,High Street\n\n");

    expect(csv.rows).toEqual([
      ["OUT-1", "Corner Shop"],
      ["OUT-2", "High Street"],
    ]);
  });

  it("pads a short row to the header's width", () => {
    // So `rows[i][c]` is always the cell under `columns[c]`. A short row that stayed short would
    // make every column after it read one to the left.
    const csv = parseCsv("code,name,channel\nOUT-1,Corner Shop\n");

    expect(csv.rows).toEqual([["OUT-1", "Corner Shop", ""]]);
  });

  it("reads a file that does not end with a newline", () => {
    expect(parseCsv("code,name\nOUT-1,Corner Shop").rows).toEqual([["OUT-1", "Corner Shop"]]);
  });

  it("reads CRLF the same as LF", () => {
    expect(parseCsv("code,name\r\nOUT-1,Corner Shop\r\n").rows).toEqual([["OUT-1", "Corner Shop"]]);
  });

  it("has nothing to say about an empty file", () => {
    expect(parseCsv("")).toEqual({ columns: [], rows: [] });
  });
});

describe("writeCsv", () => {
  it("round-trips a file that needed no quoting, unchanged", () => {
    const text = "code,name\r\nOUT-1,Corner Shop\r\n";

    expect(writeCsv(parseCsv(text))).toBe(text);
  });

  it("quotes only what has to be quoted", () => {
    const written = writeCsv({
      columns: ["code", "name", "note"],
      rows: [["OUT-1", "Smith, Jones", 'He said "hi"'], ["OUT-2", "Plain", "line\nbreak"]],
    });

    expect(written).toBe(
      'code,name,note\r\n'
      + 'OUT-1,"Smith, Jones","He said ""hi"""\r\n'
      + 'OUT-2,Plain,"line\nbreak"\r\n',
    );
  });

  it("survives a round trip through the values that need escaping", () => {
    const original = {
      columns: ["code", "name"],
      rows: [["OUT-1", 'Smith, Jones & Co "the corner"'], ["OUT-2", "two\nlines"]],
    };

    expect(parseCsv(writeCsv(original))).toEqual(original);
  });
});

describe("withCell", () => {
  it("replaces one cell and leaves every other row identical by reference", () => {
    // Identity matters: the grid renders a row per input, and rebuilding rows nobody edited would
    // throw away what someone was typing in them.
    const csv = parseCsv("code,name\nOUT-1,Corner Shop\nOUT-2,High Street\n");
    const edited = withCell(csv, 1, 1, "High Street Market");

    expect(edited.rows[1]).toEqual(["OUT-2", "High Street Market"]);
    expect(edited.rows[0]).toBe(csv.rows[0]);
  });
});

describe("fileRow", () => {
  it("counts the header, so the first data row is row 2", () => {
    // What a spreadsheet shows, and what the server reports — the number in a problem has to be one
    // the admin can navigate to.
    expect(fileRow(0)).toBe(2);
    expect(fileRow(5)).toBe(7);
  });
});
