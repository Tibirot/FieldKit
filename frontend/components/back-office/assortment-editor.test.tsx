// @vitest-environment jsdom

import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { AssortmentEditor } from "@/components/back-office/assortment-editor";
import type { AssortmentItem } from "@/lib/api/assortments";
import type { Channel } from "@/lib/api/channels";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { Product } from "@/lib/api/products";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchChannels = vi.hoisted(() => vi.fn());
const fetchProducts = vi.hoisted(() => vi.fn());
const fetchChannelAssortment = vi.hoisted(() => vi.fn());
const setChannelAssortment = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

vi.mock("@/lib/api/channels", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/channels")>()),
  fetchChannels: (...args: unknown[]) => fetchChannels(...args),
}));

vi.mock("@/lib/api/products", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/products")>()),
  fetchProducts: (...args: unknown[]) => fetchProducts(...args),
}));

vi.mock("@/lib/api/assortments", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/assortments")>()),
  fetchChannelAssortment: (...args: unknown[]) => fetchChannelAssortment(...args),
  setChannelAssortment: (...args: unknown[]) => setChannelAssortment(...args),
}));

const CHANNELS: Channel[] = [
  { id: "c-mt", name: "Modern Trade" },
  { id: "c-ho", name: "HoReCa" },
];

function product(id: string, sku: string, name: string, status: Product["status"] = "Active"): Product {
  return {
    id,
    sku,
    name,
    brandId: null,
    categoryId: null,
    taxClassId: null,
    unitOfMeasure: null,
    packSize: null,
    status,
    customFields: {},
  };
}

const PRODUCTS: Product[] = [
  product("p-still", "VRD-STL-100", "Veridian Still 1L"),
  product("p-spark", "VRD-SPK-050", "Veridian Sparkling 0.5L"),
  product("p-old", "VRD-OLD-001", "Veridian Legacy", "Discontinued"),
];

/** Modern Trade carries Still (must-stock) and nothing else. */
const MT_ASSORTMENT: AssortmentItem[] = [
  { productId: "p-still", sku: "VRD-STL-100", name: "Veridian Still 1L", mustStock: true },
];

describe("<AssortmentEditor>", () => {
  beforeEach(() => {
    fetchChannels.mockReset().mockResolvedValue(CHANNELS);
    fetchProducts.mockReset().mockResolvedValue(PRODUCTS);
    fetchChannelAssortment.mockReset().mockResolvedValue(MT_ASSORTMENT);
    setChannelAssortment.mockReset().mockResolvedValue(MT_ASSORTMENT);

    // Restored per test — one case narrows the caller, and the mock is module-level.
    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["product:read", "product:write", "channel:read"],
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

  it("starts on the first channel rather than asking which one first", async () => {
    // A selector that starts blank makes the screen look empty when it is merely unasked, and a
    // tenant with one channel would have to pick it every visit.
    render(<AssortmentEditor />);

    const channel = await screen.findByLabelText("Channel");

    expect((channel as HTMLSelectElement).value).toBe("c-mt");
    await waitFor(() => expect(fetchChannelAssortment).toHaveBeenCalledWith("token", "c-mt", expect.anything()));
  });

  it("shows the whole catalogue with what the channel already carries ticked", async () => {
    render(<AssortmentEditor />);

    const include = await screen.findByLabelText("Include Veridian Still 1L");

    expect((include as HTMLInputElement).checked).toBe(true);
    expect((screen.getByLabelText("Must stock Veridian Still 1L") as HTMLInputElement).checked).toBe(true);

    // Everything else is a row too, unticked — the screen is a picture of the decision, not a list
    // of what was already decided.
    expect((screen.getByLabelText("Include Veridian Sparkling 0.5L") as HTMLInputElement).checked).toBe(false);
  });

  it("cannot mark something must-stock that the channel does not carry", async () => {
    // The MSL is a subset of the assortment (B2), not a parallel flag.
    render(<AssortmentEditor />);

    const mustStock = await screen.findByLabelText("Must stock Veridian Sparkling 0.5L");

    expect((mustStock as HTMLInputElement).disabled).toBe(true);
  });

  it("clears the must-stock flag when a product leaves the assortment", async () => {
    // Otherwise a must-stock entry survives for a product the channel does not carry — a state B2
    // does not have — and it would resurface the next time the product was re-added.
    render(<AssortmentEditor />);

    await userEvent.click(await screen.findByLabelText("Include Veridian Still 1L"));
    await userEvent.click(screen.getByLabelText("Include Veridian Still 1L"));

    expect((screen.getByLabelText("Must stock Veridian Still 1L") as HTMLInputElement).checked).toBe(false);
  });

  it("sends the whole assortment, because the API replaces rather than merges", async () => {
    render(<AssortmentEditor />);

    await userEvent.click(await screen.findByLabelText("Include Veridian Sparkling 0.5L"));
    await userEvent.click(screen.getByRole("button", { name: "Save assortment" }));

    await waitFor(() => expect(setChannelAssortment).toHaveBeenCalled());

    expect(setChannelAssortment.mock.calls[0][1]).toBe("c-mt");
    expect(setChannelAssortment.mock.calls[0][2]).toEqual(
      expect.arrayContaining([
        { productId: "p-still", mustStock: true },
        { productId: "p-spark", mustStock: false },
      ]),
    );
    expect(setChannelAssortment.mock.calls[0][2]).toHaveLength(2);
  });

  it("offers nothing to save until something changes", async () => {
    render(<AssortmentEditor />);

    const save = await screen.findByRole("button", { name: "Save assortment" });
    expect((save as HTMLButtonElement).disabled).toBe(true);

    await userEvent.click(screen.getByLabelText("Include Veridian Sparkling 0.5L"));
    expect((save as HTMLButtonElement).disabled).toBe(false);

    // And back again — a change undone is not a change.
    await userEvent.click(screen.getByLabelText("Include Veridian Sparkling 0.5L"));
    expect((save as HTMLButtonElement).disabled).toBe(true);
  });

  it("does not carry one channel's edits into another", async () => {
    // The reason the editable half is keyed by channel rather than synced by an effect: switching
    // while mid-edit must show what HoReCa holds, not what was being done to Modern Trade.
    render(<AssortmentEditor />);

    await userEvent.click(await screen.findByLabelText("Include Veridian Sparkling 0.5L"));

    fetchChannelAssortment.mockResolvedValue([]);
    await userEvent.selectOptions(screen.getByLabelText("Channel"), "c-ho");

    await waitFor(() =>
      expect((screen.getByLabelText("Include Veridian Still 1L") as HTMLInputElement).checked).toBe(false),
    );

    expect((screen.getByLabelText("Include Veridian Sparkling 0.5L") as HTMLInputElement).checked).toBe(false);
    expect((screen.getByRole("button", { name: "Save assortment" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("counts what is in and what must be stocked", async () => {
    render(<AssortmentEditor />);

    expect((await screen.findByRole("status")).textContent).toContain("1 product");
    expect(screen.getByRole("status").textContent).toContain("1 must-stock");
  });

  it("keeps a discontinued product visible, since removing it is why someone came", async () => {
    render(<AssortmentEditor />);

    expect(await screen.findByLabelText("Include Veridian Legacy")).toBeTruthy();
    expect(screen.getByText("Discontinued")).toBeTruthy();
  });

  it("says a workspace with no channels cannot have an assortment, and where to fix it", async () => {
    // The dead end this screen would otherwise be — an assortment is authored per channel, and the
    // fix is on a different screen entirely.
    fetchChannels.mockResolvedValue([]);

    render(<AssortmentEditor />);

    expect(await screen.findByText(/an assortment is authored per channel/i)).toBeTruthy();
  });

  it("says which permission is missing rather than that something failed", async () => {
    fetchProducts.mockRejectedValue(new ApiError(403));

    render(<AssortmentEditor />);

    expect(await screen.findByText(/do not have permission/i)).toBeTruthy();
  });

  it("offers no way to change anything to a caller who may only read", async () => {
    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["product:read", "channel:read"],
    });

    render(<AssortmentEditor />);

    const include = await screen.findByLabelText("Include Veridian Still 1L");

    expect((include as HTMLInputElement).disabled).toBe(true);
    expect(screen.queryByRole("button", { name: "Save assortment" })).toBeNull();

    // Still readable: what a channel carries is worth seeing without being able to change it.
    expect((include as HTMLInputElement).checked).toBe(true);
  });

  it("passes on a refusal in the API's own words", async () => {
    setChannelAssortment.mockRejectedValue(
      new ApiError(400, [{ field: "items", message: "2 product(s) do not exist." }]),
    );

    render(<AssortmentEditor />);

    await userEvent.click(await screen.findByLabelText("Include Veridian Sparkling 0.5L"));
    await userEvent.click(screen.getByRole("button", { name: "Save assortment" }));

    expect((await screen.findByRole("alert")).textContent).toContain("2 product(s) do not exist.");
  });
});
