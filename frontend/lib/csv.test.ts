import { describe, expect, it } from "vitest";

import { roughRowCount, writeCsv } from "@/lib/csv";

describe("writeCsv", () => {
  it("writes a header and its rows", () => {
    expect(writeCsv(["code", "name"], [["OUT-1", "Corner Shop"]])).toBe(
      "code,name\r\nOUT-1,Corner Shop\r\n",
    );
  });

  it("quotes only what has to be quoted", () => {
    // A file that never needed quoting should come back looking like the one that was uploaded.
    const written = writeCsv(
      ["code", "name", "note"],
      [
        ["OUT-1", "Smith, Jones", 'He said "hi"'],
        ["OUT-2", "Plain", "line\nbreak"],
      ],
    );

    expect(written).toBe(
      "code,name,note\r\n"
      + 'OUT-1,"Smith, Jones","He said ""hi"""\r\n'
      + 'OUT-2,Plain,"line\nbreak"\r\n',
    );
  });

  it("writes an empty cell as an empty cell", () => {
    // The server reads a blank as absent, which is what it meant on the way in too.
    expect(writeCsv(["code", "segment"], [["OUT-1", ""]])).toBe("code,segment\r\nOUT-1,\r\n");
  });
});

describe("roughRowCount", () => {
  it("does not count the header, or a trailing blank line", () => {
    expect(roughRowCount("code,name\nOUT-1,Shop\nOUT-2,Shop\n")).toBe(2);
  });

  it("is allowed to be wrong about a quoted newline", () => {
    // Documented rather than fixed: reading this properly is a CSV reader, which is the thing this
    // screen deliberately does not have. Its only job is to refuse a file that is obviously far too
    // large before uploading it, and the cost of being wrong is a round trip.
    expect(roughRowCount('code,address\nOUT-1,"Two\nLines"\n')).toBe(2);
  });

  it("has nothing to count in an empty file", () => {
    expect(roughRowCount("")).toBe(0);
  });
});
