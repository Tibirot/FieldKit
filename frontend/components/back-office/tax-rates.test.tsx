// @vitest-environment jsdom

import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { TaxRates } from "@/components/back-office/tax-rates";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { TaxRate, Vocabulary } from "@/lib/api/products";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchTaxClasses = vi.hoisted(() => vi.fn());
const fetchTaxRates = vi.hoisted(() => vi.fn());
const setTaxRates = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));
vi.mock("next/navigation", () => ({ useParams: () => ({ id: "tc-1" }) }));

vi.mock("@/lib/api/products", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/products")>()),
  fetchTaxClasses: (...args: unknown[]) => fetchTaxClasses(...args),
  fetchTaxRates: (...args: unknown[]) => fetchTaxRates(...args),
  setTaxRates: (...args: unknown[]) => setTaxRates(...args),
}));

/** Waits for the permission answer — see the note in outlet-assortment.test.tsx. */
async function ready(): Promise<void> {
  await screen.findByRole("button", { name: "Save rates" });
}

const REDUCED: Vocabulary = { id: "tc-1", name: "Reduced" };

/** Trailing zeroes on purpose: the string is the value, not a rendering of one. */
const RATES: TaxRate[] = [
  { id: "r-1", countryCode: "RO", percentage: "9.00", effectiveFrom: "2026-01-01", effectiveTo: null },
];

describe("<TaxRates>", () => {
  beforeEach(() => {
    fetchTaxClasses.mockReset().mockResolvedValue([REDUCED]);
    fetchTaxRates.mockReset().mockResolvedValue(RATES);
    setTaxRates.mockReset().mockResolvedValue(RATES);

    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["product:read", "product:write"],
    });

    auth.current = {
      status: "authenticated",
      user: { access_token: "token", profile: { sub: "subject-a" } },
      workspace: "fieldkit-dev",
      signIn: vi.fn(),
      signOut: vi.fn(),
      completeSignIn: vi.fn(),
    } as unknown as AuthContextValue;
  });

  it("shows a rate exactly as the server sent it", async () => {
    render(<TaxRates />);
    await ready();

    expect((screen.getByLabelText("Percentage, rate 1") as HTMLInputElement).value).toBe("9.00");
    expect((screen.getByLabelText("Country, rate 1") as HTMLInputElement).value).toBe("RO");
  });

  it("says no rates means unknown rather than zero", async () => {
    // The difference `TaxEngine.Resolve` keeps: an empty set resolves to no tax at all, and a
    // zero-rated class is a rate of 0 somebody authored. A screen that called them the same thing
    // would make the second impossible to express.
    fetchTaxRates.mockResolvedValue([]);

    render(<TaxRates />);
    await ready();

    expect(screen.getByRole("status").textContent).toMatch(/unknown, not to zero/i);
  });

  it("sends a country upper-cased, and an open window as null", async () => {
    render(<TaxRates />);
    await ready();

    await userEvent.click(screen.getByRole("button", { name: "Add a rate" }));
    await userEvent.type(screen.getByLabelText("Country, rate 2"), "de");
    await userEvent.type(screen.getByLabelText("Percentage, rate 2"), "19.00");
    await userEvent.type(screen.getByLabelText("Applies from, rate 2"), "2026-01-01");
    await userEvent.click(screen.getByRole("button", { name: "Save rates" }));

    await waitFor(() => expect(setTaxRates).toHaveBeenCalled());

    expect(setTaxRates.mock.calls[0][2]).toEqual([
      { countryCode: "RO", percentage: "9.00", effectiveFrom: "2026-01-01", effectiveTo: null },
      { countryCode: "DE", percentage: "19.00", effectiveFrom: "2026-01-01", effectiveTo: null },
    ]);
  });

  it("catches the same country and start date twice, which the API refuses as a set", async () => {
    // A rate's identity is its country *and* its start date together — the same country twice is
    // ordinary and correct, as long as the windows differ.
    render(<TaxRates />);
    await ready();

    await userEvent.click(screen.getByRole("button", { name: "Add a rate" }));
    await userEvent.type(screen.getByLabelText("Country, rate 2"), "RO");
    await userEvent.type(screen.getByLabelText("Percentage, rate 2"), "5.00");
    await userEvent.type(screen.getByLabelText("Applies from, rate 2"), "2026-01-01");

    expect((await screen.findAllByText(/already has a rate starting/i)).length).toBeGreaterThan(0);
    expect((screen.getByRole("button", { name: "Save rates" }) as HTMLButtonElement).disabled).toBe(
      true,
    );
  });

  it("lets the same country have a second rate on a different date", async () => {
    render(<TaxRates />);
    await ready();

    await userEvent.click(screen.getByRole("button", { name: "Add a rate" }));
    await userEvent.type(screen.getByLabelText("Country, rate 2"), "RO");
    await userEvent.type(screen.getByLabelText("Percentage, rate 2"), "11.00");
    await userEvent.type(screen.getByLabelText("Applies from, rate 2"), "2027-01-01");

    expect(screen.queryByText(/already has a rate starting/i)).toBeNull();
    expect((screen.getByRole("button", { name: "Save rates" }) as HTMLButtonElement).disabled).toBe(
      false,
    );
  });

  it("refuses a comma decimal beside the row rather than sending it", async () => {
    render(<TaxRates />);
    await ready();

    await userEvent.clear(screen.getByLabelText("Percentage, rate 1"));
    await userEvent.type(screen.getByLabelText("Percentage, rate 1"), "19,00");

    expect(await screen.findByText(/19\.00, not 19,00/)).toBeTruthy();
    expect((screen.getByRole("button", { name: "Save rates" }) as HTMLButtonElement).disabled).toBe(
      true,
    );
  });

  it("refuses a country that is not two letters", async () => {
    // Three letters cannot be typed — the box is `maxLength={2}` — so the reachable mistakes are a
    // half-typed code and an emptied one. Both have to be caught, because both are what a person
    // leaves behind when they are interrupted mid-row.
    render(<TaxRates />);
    await ready();

    await userEvent.clear(screen.getByLabelText("Country, rate 1"));
    await userEvent.type(screen.getByLabelText("Country, rate 1"), "R");

    expect(await screen.findByText(/two-letter ISO-3166-1/i)).toBeTruthy();
    expect((screen.getByRole("button", { name: "Save rates" }) as HTMLButtonElement).disabled).toBe(
      true,
    );

    await userEvent.clear(screen.getByLabelText("Country, rate 1"));
    expect(await screen.findByText(/two-letter ISO-3166-1/i)).toBeTruthy();
  });

  it("refuses a window that ends before it starts", async () => {
    // Half-open, so equal dates are an empty window too — a rate that never applies.
    render(<TaxRates />);
    await ready();

    await userEvent.type(screen.getByLabelText("Applies until, rate 1"), "2026-01-01");

    expect(await screen.findByText(/ends after it starts/i)).toBeTruthy();
    expect((screen.getByRole("button", { name: "Save rates" }) as HTMLButtonElement).disabled).toBe(
      true,
    );
  });

  it("lets a class be emptied, which is how a rate is withdrawn", async () => {
    render(<TaxRates />);
    await ready();

    await userEvent.click(screen.getByRole("button", { name: "Remove rate 1" }));
    await userEvent.click(screen.getByRole("button", { name: "Save rates" }));

    await waitFor(() => expect(setTaxRates).toHaveBeenCalled());
    expect(setTaxRates.mock.calls[0][2]).toEqual([]);
  });

  it("says a tax class that does not exist is missing rather than broken", async () => {
    fetchTaxClasses.mockResolvedValue([]);

    render(<TaxRates />);

    expect(await screen.findByText(/does not exist/i)).toBeTruthy();
  });

  it("says which permission is missing rather than that something failed", async () => {
    fetchTaxRates.mockRejectedValue(new ApiError(403));

    render(<TaxRates />);

    expect(await screen.findByText(/do not have permission/i)).toBeTruthy();
  });

  it("offers no way to change anything to a caller who may only read", async () => {
    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["product:read"],
    });

    render(<TaxRates />);

    const box = await screen.findByLabelText("Percentage, rate 1");

    await waitFor(() => expect((box as HTMLInputElement).disabled).toBe(true));
    expect(screen.queryByRole("button", { name: "Save rates" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Add a rate" })).toBeNull();
    expect((box as HTMLInputElement).value).toBe("9.00");
  });

  it("says a refusal in the reader's language, not the server's", async () => {
    // The screen that most needed ADR-0012 stage 2: every one of its refusals is a Products code.
    setTaxRates.mockRejectedValue(
      new ApiError(400, [
        {
          field: "rates[0].countryCode",
          message: "A country is a two-letter ISO-3166-1 code, e.g. RO.",
          code: "product.tax.countryInvalid",
        },
      ]),
    );

    render(<TaxRates />);
    await ready();

    await userEvent.clear(screen.getByLabelText("Percentage, rate 1"));
    await userEvent.type(screen.getByLabelText("Percentage, rate 1"), "10.00");
    await userEvent.click(screen.getByRole("button", { name: "Save rates" }));

    expect((await screen.findByRole("alert")).textContent).toContain("ISO-3166-1");
  });
});
