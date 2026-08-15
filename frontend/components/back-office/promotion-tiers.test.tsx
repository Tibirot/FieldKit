// @vitest-environment jsdom

import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { PromotionTiers } from "@/components/back-office/promotion-tiers";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { Promotion, PromotionTier } from "@/lib/api/promotions";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchPromotions = vi.hoisted(() => vi.fn());
const fetchTiers = vi.hoisted(() => vi.fn());
const setTiers = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));
vi.mock("next/navigation", () => ({ useParams: () => ({ id: "promo-1" }) }));

// The header now renders a <Breadcrumb>, which reads the path through next-intl. Stubbed the way
// the navigation's own tests stub it, so these assertions stay about this screen.
vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children }: { href: string; children: React.ReactNode }) => (
    <a href={href}>{children}</a>
  ),
  usePathname: () => "/products/promotions/promo-1/tiers",
}));

vi.mock("@/lib/api/promotions", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/promotions")>()),
  fetchPromotions: (...args: unknown[]) => fetchPromotions(...args),
  fetchTiers: (...args: unknown[]) => fetchTiers(...args),
  setTiers: (...args: unknown[]) => setTiers(...args),
}));

/** Waits for the permission answer — see the note in outlet-assortment.test.tsx. */
async function ready(): Promise<void> {
  await screen.findByRole("button", { name: "Save tiers" });
}

function promotion(over: Partial<Promotion> = {}): Promotion {
  return {
    id: "promo-1",
    name: "Case ladder",
    type: "VolumeTiered",
    value: null,
    currency: null,
    validFrom: "2026-06-01",
    validTo: null,
    priority: 10,
    bundle: null,
    ...over,
  };
}

/** Trailing zeroes on purpose: the string is the value, not a rendering of one. */
const TIERS: PromotionTier[] = [
  { minQuantity: 6, value: "5.00", currency: null },
  { minQuantity: 12, value: "7.50", currency: null },
];

describe("<PromotionTiers>", () => {
  beforeEach(() => {
    fetchPromotions.mockReset().mockResolvedValue([promotion()]);
    fetchTiers.mockReset().mockResolvedValue(TIERS);
    setTiers.mockReset().mockResolvedValue(TIERS);

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

  it("shows a discount exactly as the server sent it", async () => {
    render(<PromotionTiers />);
    await ready();

    expect((screen.getByLabelText("Discount, tier 1") as HTMLInputElement).value).toBe("5.00");
    expect((screen.getByLabelText("Discount, tier 2") as HTMLInputElement).value).toBe("7.50");
  });

  it("refuses to edit tiers on a type that has none", async () => {
    // A flat promotion with tiers would carry two discounts and no rule saying which applies, so
    // the API refuses the pairing. The route is still reachable by typing an id, and saying which
    // type this is beats an editor that would refuse everything saved into it.
    fetchPromotions.mockResolvedValue([promotion({ type: "PercentOff", value: "10.00" })]);

    render(<PromotionTiers />);

    // Named as the rest of the back office names it, not as the enum on the wire.
    expect(await screen.findByText(/This one is Percentage off/i)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Save tiers" })).toBeNull();
  });

  it("asks for the kind once, not once per row", async () => {
    // The API refuses a set mixing percentages and amounts. Letting an author build one and then
    // be told would be teaching a rule by refusing it; there is one control for the promotion.
    render(<PromotionTiers />);
    await ready();

    expect(screen.getAllByRole("radio")).toHaveLength(2);
    expect(screen.queryByLabelText("Currency")).toBeNull();

    await userEvent.click(screen.getByLabelText("Fixed amounts, in"));
    expect(screen.getByLabelText("Currency")).toBeTruthy();
  });

  it("opens as an amount editor when the stored tiers are amounts", async () => {
    // Read off what is stored rather than defaulted: a promotion already priced in euros should not
    // present itself as a percentage one, which a save would then quietly make it.
    fetchTiers.mockResolvedValue([{ minQuantity: 6, value: "3.00", currency: "EUR" }]);

    render(<PromotionTiers />);
    await ready();

    expect((screen.getByLabelText("Fixed amounts, in") as HTMLInputElement).checked).toBe(true);
    expect((screen.getByLabelText("Currency") as HTMLInputElement).value).toBe("EUR");
  });

  it("puts the one currency on every tier it sends", async () => {
    fetchTiers.mockResolvedValue([{ minQuantity: 6, value: "3.00", currency: "eur" }]);

    render(<PromotionTiers />);
    await ready();

    await userEvent.click(screen.getByRole("button", { name: "Add a tier" }));
    await userEvent.type(screen.getByLabelText("Smallest quantity, tier 2"), "12");
    await userEvent.type(screen.getByLabelText("Discount, tier 2"), "5.00");
    await userEvent.click(screen.getByRole("button", { name: "Save tiers" }));

    await waitFor(() => expect(setTiers).toHaveBeenCalled());

    expect(setTiers.mock.calls[0][2]).toEqual([
      { minQuantity: 6, value: "3.00", currency: "EUR" },
      { minQuantity: 12, value: "5.00", currency: "EUR" },
    ]);
  });

  it("clears the currency when the tiers become percentages", async () => {
    // Null is what the API reads as "this is a percentage"; sending a leftover "EUR" alongside one
    // would be refused, and sending "" would be refused differently.
    fetchTiers.mockResolvedValue([{ minQuantity: 6, value: "3.00", currency: "EUR" }]);

    render(<PromotionTiers />);
    await ready();

    await userEvent.click(screen.getByLabelText("Percentages"));
    await userEvent.click(screen.getByRole("button", { name: "Save tiers" }));

    await waitFor(() => expect(setTiers).toHaveBeenCalled());
    expect(setTiers.mock.calls[0][2]).toEqual([{ minQuantity: 6, value: "3.00", currency: null }]);
  });

  it("refuses a threshold of one beside the row rather than on save", async () => {
    // "Buy one or more" is every line that matched at all — a flat discount wearing a tier's
    // clothes, and one that would silently shadow the PercentOff type it duplicates.
    render(<PromotionTiers />);
    await ready();

    await userEvent.clear(screen.getByLabelText("Smallest quantity, tier 1"));
    await userEvent.type(screen.getByLabelText("Smallest quantity, tier 1"), "1");

    expect(await screen.findByText(/a tier at 1 is a flat discount/i)).toBeTruthy();
    expect((screen.getByRole("button", { name: "Save tiers" }) as HTMLButtonElement).disabled).toBe(
      true,
    );

    await userEvent.click(screen.getByRole("button", { name: "Save tiers" }));
    expect(setTiers).not.toHaveBeenCalled();
  });

  it("catches a repeated threshold, which the API would refuse as a set", async () => {
    render(<PromotionTiers />);
    await ready();

    await userEvent.clear(screen.getByLabelText("Smallest quantity, tier 2"));
    await userEvent.type(screen.getByLabelText("Smallest quantity, tier 2"), "6");

    expect((await screen.findAllByText(/already has a tier/i)).length).toBeGreaterThan(0);
    expect((screen.getByRole("button", { name: "Save tiers" }) as HTMLButtonElement).disabled).toBe(
      true,
    );
  });

  it("refuses a comma decimal beside the row rather than sending it", async () => {
    render(<PromotionTiers />);
    await ready();

    await userEvent.clear(screen.getByLabelText("Discount, tier 1"));
    await userEvent.type(screen.getByLabelText("Discount, tier 1"), "12,50");

    expect(await screen.findByText(/12\.50, not 12,50/)).toBeTruthy();
    expect((screen.getByRole("button", { name: "Save tiers" }) as HTMLButtonElement).disabled).toBe(
      true,
    );
  });

  it("refuses an amount kind without a currency", async () => {
    render(<PromotionTiers />);
    await ready();

    await userEvent.click(screen.getByLabelText("Fixed amounts, in"));

    expect(await screen.findByText(/Three letters, ISO 4217/i)).toBeTruthy();
    expect((screen.getByRole("button", { name: "Save tiers" }) as HTMLButtonElement).disabled).toBe(
      true,
    );
  });

  it("renumbers the rows when one above is removed", async () => {
    render(<PromotionTiers />);
    await ready();

    await userEvent.click(screen.getByRole("button", { name: "Remove tier 1" }));

    expect((screen.getByLabelText("Smallest quantity, tier 1") as HTMLInputElement).value).toBe("12");
    expect((screen.getByLabelText("Discount, tier 1") as HTMLInputElement).value).toBe("7.50");
    expect(screen.queryByLabelText("Discount, tier 2")).toBeNull();
  });

  it("lets a tiered promotion be emptied, which is how it stops discounting", async () => {
    render(<PromotionTiers />);
    await ready();

    await userEvent.click(screen.getByRole("button", { name: "Remove tier 2" }));
    await userEvent.click(screen.getByRole("button", { name: "Remove tier 1" }));

    expect(screen.getByRole("status").textContent).toMatch(/discounts nothing/i);

    await userEvent.click(screen.getByRole("button", { name: "Save tiers" }));

    await waitFor(() => expect(setTiers).toHaveBeenCalled());
    expect(setTiers.mock.calls[0][2]).toEqual([]);
  });

  it("offers nothing to save until a tier changes", async () => {
    render(<PromotionTiers />);

    const save = await screen.findByRole("button", { name: "Save tiers" });
    expect((save as HTMLButtonElement).disabled).toBe(true);

    await userEvent.clear(screen.getByLabelText("Discount, tier 1"));
    await userEvent.type(screen.getByLabelText("Discount, tier 1"), "6.00");
    expect((save as HTMLButtonElement).disabled).toBe(false);
  });

  it("counts what is there", async () => {
    render(<PromotionTiers />);
    await ready();

    expect(screen.getByRole("status").textContent).toContain("2 thresholds");
  });

  it("says a promotion that does not exist is missing rather than broken", async () => {
    fetchPromotions.mockResolvedValue([]);

    render(<PromotionTiers />);

    expect(await screen.findByText(/does not exist/i)).toBeTruthy();
  });

  it("says which permission is missing rather than that something failed", async () => {
    fetchTiers.mockRejectedValue(new ApiError(403));

    render(<PromotionTiers />);

    expect(await screen.findByText(/do not have permission/i)).toBeTruthy();
  });

  it("offers no way to change anything to a caller who may only read", async () => {
    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["product:read"],
    });

    render(<PromotionTiers />);

    const box = await screen.findByLabelText("Discount, tier 1");

    await waitFor(() => expect((box as HTMLInputElement).disabled).toBe(true));
    expect(screen.queryByRole("button", { name: "Save tiers" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Add a tier" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Remove tier 1" })).toBeNull();

    // Still readable: what the ladder is, is the question a reader came to answer.
    expect((box as HTMLInputElement).value).toBe("5.00");
  });

  it("passes on a refusal in the API's own words", async () => {
    setTiers.mockRejectedValue(
      new ApiError(400, [
        { field: "tiers", message: "Every tier of one promotion is a percentage, or every tier is an amount." },
      ]),
    );

    render(<PromotionTiers />);
    await ready();

    await userEvent.clear(screen.getByLabelText("Discount, tier 1"));
    await userEvent.type(screen.getByLabelText("Discount, tier 1"), "6.00");
    await userEvent.click(screen.getByRole("button", { name: "Save tiers" }));

    expect((await screen.findByRole("alert")).textContent).toContain("percentage");
  });
});
