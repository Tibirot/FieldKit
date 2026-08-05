// @vitest-environment jsdom

import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { OutletImport } from "@/components/back-office/outlet-import";
import { ApiError } from "@/lib/api/client";
import type { OutletImportResult } from "@/lib/api/outlet-import";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchImportCapabilities = vi.hoisted(() => vi.fn());
const importOutlets = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

vi.mock("@/lib/api/outlet-import", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/outlet-import")>()),
  fetchImportCapabilities: (...args: unknown[]) => fetchImportCapabilities(...args),
  importOutlets: (...args: unknown[]) => importOutlets(...args),
}));

const CLEAN: OutletImportResult = {
  totalRows: 2,
  accepted: 2,
  rejected: 0,
  imported: 0,
  dryRun: true,
  mode: "AllOrNothing",
  problems: [],
  rejectedRowsCsv: null,
  ignoredColumns: [],
};

/** A file the browser has read, as the input's change handler will see it. */
function csv(rows: number, name = "outlets.csv") {
  const lines = ["code,name,channel,time_zone"];
  for (let row = 1; row <= rows; row++) lines.push(`OUT-${row},Shop ${row},Modern Trade,Europe/Bucharest`);

  return new File([lines.join("\n")], name, { type: "text/csv" });
}

/** The file text the last import call uploaded. */
const sent = () => (importOutlets.mock.calls.at(-1)?.[1] as { text: string }).text;

/** What the last import call was asked to do. */
const options = () => importOutlets.mock.calls.at(-1)?.[2] as { mode: string; dryRun: boolean };

describe("<OutletImport>", () => {
  beforeEach(() => {
    fetchImportCapabilities.mockReset().mockResolvedValue({
      maxRows: 5000,
      mediaTypes: ["text/csv"],
      reasonColumn: "import_error",
    });
    importOutlets.mockReset().mockResolvedValue(CLEAN);

    auth.current = {
      status: "authenticated",
      user: { access_token: "token", profile: { sub: "subject-a" } },
      workspace: "fieldkit-dev",
      signIn: vi.fn(),
      signOut: vi.fn(),
      completeSignIn: vi.fn(),
    } as unknown as AuthContextValue;
  });

  it("checks before it applies, and writes nothing until asked", async () => {
    // The dry run executes every rule and writes nothing, and returns exactly what the real run
    // would — which is what makes what is on screen an answer rather than a preview to be trusted.
    render(<OutletImport />);
    await waitFor(() => expect(fetchImportCapabilities).toHaveBeenCalled());

    await userEvent.upload(screen.getByLabelText(/^file/i), csv(2));
    await userEvent.click(screen.getByRole("button", { name: "Check file" }));

    await waitFor(() => expect(importOutlets).toHaveBeenCalled());
    expect(options().dryRun).toBe(true);

    await userEvent.click(screen.getByRole("button", { name: "Apply" }));

    await waitFor(() => expect(importOutlets).toHaveBeenCalledTimes(2));
    expect(options().dryRun).toBe(false);
  });

  it("will not apply a file nobody has checked", async () => {
    // "Apply" straight from a file picker is the mode this endpoint deliberately does not have, and
    // offering it on screen would put it back.
    render(<OutletImport />);
    await waitFor(() => expect(fetchImportCapabilities).toHaveBeenCalled());

    await userEvent.upload(screen.getByLabelText(/^file/i), csv(2));

    expect(screen.getByRole<HTMLButtonElement>("button", { name: "Apply" }).disabled).toBe(true);
  });

  it("refuses an oversized file without uploading it", async () => {
    // Twelve megabytes, sent to be told what could have been said before it left. The cap comes from
    // the server, so this cannot disagree with what would actually happen.
    fetchImportCapabilities.mockResolvedValue({
      maxRows: 3,
      mediaTypes: ["text/csv"],
      reasonColumn: "import_error",
    });

    render(<OutletImport />);
    await waitFor(() => expect(fetchImportCapabilities).toHaveBeenCalled());

    await userEvent.upload(screen.getByLabelText(/^file/i), csv(5));

    expect((await screen.findByRole("alert")).textContent).toContain("5 rows");
    expect(screen.getByRole<HTMLButtonElement>("button", { name: "Check file" }).disabled).toBe(true);
    expect(importOutlets).not.toHaveBeenCalled();
  });

  it("says what is valid, what is wrong, and what is in the database — separately", async () => {
    // Three numbers because there are three sentences. After an AllOrNothing run that hit one bad
    // row, `imported` is 0 while `accepted` is not, and a screen that collapsed them would claim an
    // import that did not happen.
    importOutlets.mockResolvedValue({
      ...CLEAN,
      dryRun: false,
      totalRows: 3,
      accepted: 2,
      rejected: 1,
      imported: 0,
      problems: [{ row: 3, column: "channel", message: "'Modren Trade' is not a channel." }],
    });

    render(<OutletImport />);
    await waitFor(() => expect(fetchImportCapabilities).toHaveBeenCalled());

    await userEvent.upload(screen.getByLabelText(/^file/i), csv(3));
    await userEvent.click(screen.getByRole("button", { name: "Check file" }));

    expect(await screen.findByText(/No outlets imported, 1 row refused/)).toBeTruthy();
    expect(screen.getByText(/Nothing was written/)).toBeTruthy();
  });

  it("shows each refused row against the line number the file has", async () => {
    // `row` is the line in the uploaded file, header included, so it matches what the admin's
    // spreadsheet shows rather than an index into something they cannot see.
    importOutlets.mockResolvedValue({
      ...CLEAN,
      accepted: 1,
      rejected: 2,
      problems: [
        { row: 2, column: "time_zone", message: "'Europe/Bucuresti' is not a known time zone." },
        { row: 7, column: null, message: "This row has no code." },
      ],
    });

    render(<OutletImport />);
    await waitFor(() => expect(fetchImportCapabilities).toHaveBeenCalled());

    await userEvent.upload(screen.getByLabelText(/^file/i), csv(8));
    await userEvent.click(screen.getByRole("button", { name: "Check file" }));

    const rows = await screen.findAllByRole("row");
    const cells = rows.slice(1).map((row) => [...row.querySelectorAll("td")].map((c) => c.textContent));

    expect(cells).toEqual([
      ["2", "time_zone", "'Europe/Bucuresti' is not a known time zone."],
      ["7", "—", "This row has no code."],
    ]);
  });

  it("names the columns it ignored", async () => {
    // A real export is full of `legacy_id`, so refusing the file would be hostile — but a mistyped
    // custom-field header looks identical, and silence is how a column of data goes missing.
    importOutlets.mockResolvedValue({ ...CLEAN, ignoredColumns: ["legacy_id", "chiler_count"] });

    render(<OutletImport />);
    await waitFor(() => expect(fetchImportCapabilities).toHaveBeenCalled());

    await userEvent.upload(screen.getByLabelText(/^file/i), csv(2));
    await userEvent.click(screen.getByRole("button", { name: "Check file" }));

    expect((await screen.findByText(/were ignored/)).textContent).toContain("legacy_id, chiler_count");
  });

  it("offers the refused rows back only when there are some", async () => {
    // The escape hatch, and what stops a partial import being a trap: re-sending the original would
    // collide with everything that landed.
    render(<OutletImport />);
    await waitFor(() => expect(fetchImportCapabilities).toHaveBeenCalled());

    await userEvent.upload(screen.getByLabelText(/^file/i), csv(2));
    await userEvent.click(screen.getByRole("button", { name: "Check file" }));

    await screen.findByText(/2 rows are ready to import/);
    expect(screen.queryByRole("button", { name: /refused rows/i })).toBeNull();

    importOutlets.mockResolvedValue({
      ...CLEAN,
      rejected: 1,
      rejectedRowsCsv: "code,name,import_error\nOUT-1,Shop,'x' is not a channel.\n",
    });

    await userEvent.click(screen.getByRole("button", { name: "Check file" }));

    expect(await screen.findByRole("button", { name: /refused rows/i })).toBeTruthy();
  });

  it("carries the mode the admin chose", async () => {
    // AllOrNothing by default: of two modes that are each right for some file, the one to get by
    // omission is the one that cannot half-apply.
    render(<OutletImport />);
    await waitFor(() => expect(fetchImportCapabilities).toHaveBeenCalled());

    await userEvent.upload(screen.getByLabelText(/^file/i), csv(2));
    await userEvent.click(screen.getByRole("button", { name: "Check file" }));
    await waitFor(() => expect(importOutlets).toHaveBeenCalled());

    expect(options().mode).toBe("AllOrNothing");

    await userEvent.click(screen.getByRole("radio", { name: /Import the good rows/ }));
    await userEvent.click(screen.getByRole("button", { name: "Check file" }));

    await waitFor(() => expect(importOutlets).toHaveBeenCalledTimes(2));
    expect(options().mode).toBe("Partial");
  });

  it("offers the refused rows for correction, flagging the cell each problem names", async () => {
    // No API of its own, which is the evidence the response shape was right: `problems` already
    // carry {row, column, message} and `row` already matches the file's own numbering.
    importOutlets.mockResolvedValue({
      ...CLEAN,
      totalRows: 3,
      accepted: 2,
      rejected: 1,
      problems: [{ row: 3, column: "channel", message: "'Modren Trade' is not a channel." }],
    });

    render(<OutletImport />);
    await waitFor(() => expect(fetchImportCapabilities).toHaveBeenCalled());

    await userEvent.upload(screen.getByLabelText(/^file/i), csv(3));
    await userEvent.click(screen.getByRole("button", { name: "Check file" }));

    // Only the refused row. The other two are correct, and scrolling past them is a way of hiding
    // the one that needs attention.
    const cells = await screen.findAllByLabelText(/, row 3$/);

    expect(cells.map((cell) => (cell as HTMLInputElement).value)).toEqual([
      "OUT-2",
      "Shop 2",
      "Modern Trade",
      "Europe/Bucharest",
    ]);

    expect(screen.getByLabelText("channel, row 3").getAttribute("aria-invalid")).toBe("true");
    expect(screen.getByLabelText("code, row 3").getAttribute("aria-invalid")).toBe("false");
    expect(screen.queryByLabelText("code, row 2")).toBeNull();
  });

  it("sends the corrected file when it checks again", async () => {
    // The whole point. Correcting a cell means re-serialising the file that has been in the browser
    // the whole time, and checking that — not the one that was uploaded.
    importOutlets.mockResolvedValue({
      ...CLEAN,
      totalRows: 2,
      accepted: 1,
      rejected: 1,
      problems: [{ row: 2, column: "channel", message: "'Modren Trade' is not a channel." }],
    });

    render(<OutletImport />);
    await waitFor(() => expect(fetchImportCapabilities).toHaveBeenCalled());

    await userEvent.upload(screen.getByLabelText(/^file/i), csv(2));
    await userEvent.click(screen.getByRole("button", { name: "Check file" }));

    const cell = await screen.findByLabelText("name, row 2");

    await userEvent.clear(cell);
    await userEvent.type(cell, "Corrected Shop");
    await userEvent.click(screen.getByRole("button", { name: "Check again" }));

    await waitFor(() => expect(importOutlets).toHaveBeenCalledTimes(2));

    expect(sent()).toContain("Corrected Shop");
    expect(sent()).not.toContain("Shop 1");
  });

  it("sends the file exactly as uploaded until something is edited", async () => {
    // "Check the file I gave you" should mean the file they gave us — re-serialising an untouched
    // file would quietly send bytes nobody chose.
    render(<OutletImport />);
    await waitFor(() => expect(fetchImportCapabilities).toHaveBeenCalled());

    const file = csv(2);

    await userEvent.upload(screen.getByLabelText(/^file/i), file);
    await userEvent.click(screen.getByRole("button", { name: "Check file" }));

    await waitFor(() => expect(importOutlets).toHaveBeenCalled());

    expect(sent()).toBe(await file.text());
  });

  it("will not offer the grid when it read a different number of rows than the server", async () => {
    // Two CSV readers, one in C# and one here, and the grid depends on them agreeing about which row
    // is row 7. A flag on the wrong shop is worse than no grid, so the counts are compared first and
    // the download stays as the answer.
    importOutlets.mockResolvedValue({
      ...CLEAN,
      totalRows: 99,
      accepted: 0,
      rejected: 1,
      problems: [{ row: 2, column: "code", message: "This row has no code." }],
      rejectedRowsCsv: "code,name,import_error\n",
    });

    render(<OutletImport />);
    await waitFor(() => expect(fetchImportCapabilities).toHaveBeenCalled());

    await userEvent.upload(screen.getByLabelText(/^file/i), csv(2));
    await userEvent.click(screen.getByRole("button", { name: "Check file" }));

    await screen.findByRole("button", { name: /refused rows/i });

    expect(screen.queryByRole("button", { name: "Check again" })).toBeNull();
    expect(screen.queryByLabelText("code, row 2")).toBeNull();
  });

  it("shows a problem about no column in particular beside its row", async () => {
    // "This row has no code" is about the row, and pinning it to a box at random would send someone
    // looking at the wrong cell.
    importOutlets.mockResolvedValue({
      ...CLEAN,
      totalRows: 2,
      accepted: 1,
      rejected: 1,
      problems: [{ row: 2, column: null, message: "This row is empty." }],
    });

    render(<OutletImport />);
    await waitFor(() => expect(fetchImportCapabilities).toHaveBeenCalled());

    await userEvent.upload(screen.getByLabelText(/^file/i), csv(2));
    await userEvent.click(screen.getByRole("button", { name: "Check file" }));

    expect(await screen.findByLabelText("code, row 2")).toBeTruthy();
    expect(screen.getAllByText("This row is empty.").length).toBeGreaterThan(0);
    expect(screen.getByLabelText("code, row 2").getAttribute("aria-invalid")).toBe("false");
  });

  it("does not flatten a cell that holds more than one line", async () => {
    // `<input value>` is sanitised by the DOM: it drops newlines. A quoted address over two lines
    // would show as one and flatten for real the moment anyone typed in that row.
    const file = new File(
      ['code,name,address\nOUT-1,"Two\nLine Shop","Str. Dorobanti 1\nBucharest"\n'],
      "outlets.csv",
      { type: "text/csv" },
    );

    importOutlets.mockResolvedValue({
      ...CLEAN,
      totalRows: 1,
      accepted: 0,
      rejected: 1,
      problems: [{ row: 2, column: "code", message: "This code already exists." }],
    });

    render(<OutletImport />);
    await waitFor(() => expect(fetchImportCapabilities).toHaveBeenCalled());

    await userEvent.upload(screen.getByLabelText(/^file/i), file);
    await userEvent.click(screen.getByRole("button", { name: "Check file" }));

    const cell = await screen.findByLabelText("address, row 2");

    expect(cell.tagName).toBe("TEXTAREA");
    expect((cell as HTMLTextAreaElement).value).toBe("Str. Dorobanti 1\nBucharest");

    // And it survives a correction elsewhere, which is when the file gets written back out.
    await userEvent.type(screen.getByLabelText("code, row 2"), "-A");
    await userEvent.click(screen.getByRole("button", { name: "Check again" }));

    await waitFor(() => expect(importOutlets).toHaveBeenCalledTimes(2));
    expect(sent()).toContain('"Str. Dorobanti 1\nBucharest"');
  });

  it("keeps what the server said when it refuses the file outright", async () => {
    // A file that is not CSV has nothing to say per row, and replacing the one fact that matters
    // with "something went wrong" throws away the only description of what is actually wrong.
    importOutlets.mockRejectedValue(
      new ApiError(415, [{ field: null, message: "Send the file as text/csv." }]),
    );

    render(<OutletImport />);
    await waitFor(() => expect(fetchImportCapabilities).toHaveBeenCalled());

    await userEvent.upload(screen.getByLabelText(/^file/i), csv(2));
    await userEvent.click(screen.getByRole("button", { name: "Check file" }));

    expect((await screen.findByRole("alert")).textContent).toContain("Send the file as text/csv.");
  });
});
