// @vitest-environment jsdom

import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { OutletAssortment } from "@/components/back-office/outlet-assortment";
import type { AssortmentItem, AssortmentOverride } from "@/lib/api/assortments";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { OutletDetail } from "@/lib/api/outlets";
import type { Product } from "@/lib/api/products";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchOutlet = vi.hoisted(() => vi.fn());
const fetchProducts = vi.hoisted(() => vi.fn());
const fetchChannelAssortment = vi.hoisted(() => vi.fn());
const fetchOutletAssortment = vi.hoisted(() => vi.fn());
const fetchOutletOverrides = vi.hoisted(() => vi.fn());
const setOutletOverrides = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));
vi.mock("next/navigation", () => ({ useParams: () => ({ id: "o-1" }) }));

vi.mock("@/lib/api/outlets", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/outlets")>()),
  fetchOutlet: (...args: unknown[]) => fetchOutlet(...args),
}));

vi.mock("@/lib/api/products", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/products")>()),
  fetchProducts: (...args: unknown[]) => fetchProducts(...args),
}));

vi.mock("@/lib/api/assortments", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/assortments")>()),
  fetchChannelAssortment: (...args: unknown[]) => fetchChannelAssortment(...args),
  fetchOutletAssortment: (...args: unknown[]) => fetchOutletAssortment(...args),
  fetchOutletOverrides: (...args: unknown[]) => fetchOutletOverrides(...args),
  setOutletOverrides: (...args: unknown[]) => setOutletOverrides(...args),
}));

/**
 * Waits until the permission answer has arrived.
 *
 * `usePermissions` counts pending as denied, so every control is disabled until /api/auth/whoami
 * resolves — which is correct, and which means a test that reaches for a control as soon as the
 * *data* queries settle silently interacts with a disabled element and asserts on nothing. The Save
 * button only renders for a writer, so its presence is the signal.
 */
async function ready(): Promise<void> {
  await screen.findByRole("button", { name: "Save overrides" });
}

const OUTLET = {
  id: "o-1",
  code: "RO-BUC-0001",
  name: "Mega Image Dorobanți",
  channelId: "c-mt",
  channelName: "Modern Trade",
  status: "Active",
  timeZoneId: "Europe/Bucharest",
  customFields: {},
  contacts: [],
} as unknown as OutletDetail;

function product(id: string, sku: string, name: string): Product {
  return {
    id,
    sku,
    name,
    brandId: null,
    categoryId: null,
    taxClassId: null,
    unitOfMeasure: null,
    packSize: null,
    status: "Active",
    customFields: {},
  };
}

const PRODUCTS: Product[] = [
  product("p-still", "VRD-STL-100", "Veridian Still 1L"),
  product("p-spark", "VRD-SPK-050", "Veridian Sparkling 0.5L"),
];

/** The channel carries Still, must-stock. It does not carry Sparkling. */
const CHANNEL: AssortmentItem[] = [
  { productId: "p-still", sku: "VRD-STL-100", name: "Veridian Still 1L", mustStock: true },
];

describe("<OutletAssortment>", () => {
  beforeEach(() => {
    fetchOutlet.mockReset().mockResolvedValue(OUTLET);
    fetchProducts.mockReset().mockResolvedValue(PRODUCTS);
    fetchChannelAssortment.mockReset().mockResolvedValue(CHANNEL);
    fetchOutletAssortment.mockReset().mockResolvedValue(CHANNEL);
    fetchOutletOverrides.mockReset().mockResolvedValue([] as AssortmentOverride[]);
    setOutletOverrides.mockReset().mockResolvedValue([]);

    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["product:read", "product:write", "outlet:read"],
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

  it("says what the channel decides before what this shop changed", async () => {
    render(<OutletAssortment />);

    expect(await screen.findByText(/Its channel — Modern Trade — decides the baseline/)).toBeTruthy();
    expect(screen.getByText("Channel carries it · must stock")).toBeTruthy();
    expect(screen.getByText("Channel does not carry it")).toBeTruthy();
  });

  it("starts every product following its channel", async () => {
    render(<OutletAssortment />);

    const still = await screen.findByLabelText("Assortment for Veridian Still 1L");

    expect((still as HTMLSelectElement).value).toBe("");
    expect((screen.getByLabelText("Assortment for Veridian Sparkling 0.5L") as HTMLSelectElement).value).toBe("");
  });

  it("cannot remove something the channel does not carry", async () => {
    // There is nothing to take away. The API would store the row and it would do nothing — a
    // control that cannot act.
    render(<OutletAssortment />);

    const options = Array.from(
      (await screen.findByLabelText("Assortment for Veridian Sparkling 0.5L")).querySelectorAll("option"),
    );

    expect(options.find((o) => o.value === "Removed")?.disabled).toBe(true);
    expect(options.find((o) => o.value === "Removed")?.disabled).not.toBe(undefined);

    // And it is offerable for the one the channel does carry.
    const carried = Array.from(
      screen.getByLabelText("Assortment for Veridian Still 1L").querySelectorAll("option"),
    );

    expect(carried.find((o) => o.value === "Removed")?.disabled).toBe(false);
  });

  it("labels Add differently when the channel already carries the product", async () => {
    // The same override kind means two things — stock something new, or disagree about its terms —
    // and the label is what tells them apart.
    render(<OutletAssortment />);

    await screen.findByLabelText("Assortment for Veridian Still 1L");

    expect(screen.getByRole("option", { name: "Add here (change terms)" })).toBeTruthy();
    expect(screen.getByRole("option", { name: "Add here" })).toBeTruthy();
  });

  it("adds a product the channel does not carry", async () => {
    render(<OutletAssortment />);
    await ready();

    await userEvent.selectOptions(
      screen.getByLabelText("Assortment for Veridian Sparkling 0.5L"),
      "Added",
    );

    await userEvent.click(screen.getByLabelText("Must stock Veridian Sparkling 0.5L"));
    await userEvent.click(screen.getByRole("button", { name: "Save overrides" }));

    await waitFor(() => expect(setOutletOverrides).toHaveBeenCalled());

    expect(setOutletOverrides.mock.calls[0][1]).toBe("o-1");
    expect(setOutletOverrides.mock.calls[0][2]).toEqual([
      { productId: "p-spark", kind: "Added", mustStock: true },
    ]);
  });

  it("seeds an Add from what the channel already says, rather than clearing it", async () => {
    // Switching a carried product to Added is a change of terms. Starting it at "not must-stock"
    // would silently drop the channel's decision as a side effect of opening the control.
    render(<OutletAssortment />);
    await ready();

    await userEvent.selectOptions(
      screen.getByLabelText("Assortment for Veridian Still 1L"),
      "Added",
    );

    expect((screen.getByLabelText("Must stock Veridian Still 1L") as HTMLInputElement).checked).toBe(true);
  });

  it("only lets an Added override carry must-stock terms", async () => {
    // Following the channel means following its flag too; a removal has nothing to qualify.
    render(<OutletAssortment />);
    await ready();

    const mustStock = screen.getByLabelText("Must stock Veridian Still 1L");
    expect((mustStock as HTMLInputElement).disabled).toBe(true);

    await userEvent.selectOptions(screen.getByLabelText("Assortment for Veridian Still 1L"), "Removed");
    expect((screen.getByLabelText("Must stock Veridian Still 1L") as HTMLInputElement).disabled).toBe(true);

    await userEvent.selectOptions(screen.getByLabelText("Assortment for Veridian Still 1L"), "Added");
    expect((screen.getByLabelText("Must stock Veridian Still 1L") as HTMLInputElement).disabled).toBe(false);
  });

  it("sends nothing for a product returned to following its channel", async () => {
    fetchOutletOverrides.mockResolvedValue([
      { productId: "p-spark", sku: "VRD-SPK-050", name: "Veridian Sparkling 0.5L", kind: "Added", mustStock: false },
    ]);

    render(<OutletAssortment />);
    await ready();

    await userEvent.selectOptions(
      screen.getByLabelText("Assortment for Veridian Sparkling 0.5L"),
      "",
    );

    await userEvent.click(screen.getByRole("button", { name: "Save overrides" }));

    await waitFor(() => expect(setOutletOverrides).toHaveBeenCalled());
    expect(setOutletOverrides.mock.calls[0][2]).toEqual([]);
  });

  it("offers nothing to save until something changes", async () => {
    render(<OutletAssortment />);

    const save = await screen.findByRole("button", { name: "Save overrides" });
    expect((save as HTMLButtonElement).disabled).toBe(true);

    await userEvent.selectOptions(screen.getByLabelText("Assortment for Veridian Still 1L"), "Removed");
    expect((save as HTMLButtonElement).disabled).toBe(false);

    await userEvent.selectOptions(screen.getByLabelText("Assortment for Veridian Still 1L"), "");
    expect((save as HTMLButtonElement).disabled).toBe(true);
  });

  it("counts the effective assortment as the server last returned it", async () => {
    // Deliberately not recomputed from unsaved edits: channel minus removals plus additions is
    // PRD-02's rule and it lives on the server. A number that moved with local edits would look
    // authoritative and be a second implementation.
    render(<OutletAssortment />);

    await ready();

    expect(screen.getByRole("status").textContent).toContain("1 product sold here");
    expect(screen.getByRole("status").textContent).toContain("no overrides");

    await userEvent.selectOptions(screen.getByLabelText("Assortment for Veridian Sparkling 0.5L"), "Added");

    expect(screen.getByRole("status").textContent).toContain("1 product sold here");
    expect(screen.getByRole("status").textContent).toContain("1 override");
  });

  it("says an outlet that does not exist is missing rather than broken", async () => {
    fetchOutlet.mockRejectedValue(new ApiError(404));

    render(<OutletAssortment />);

    expect(await screen.findByText(/does not exist/i)).toBeTruthy();
  });

  it("says which permission is missing rather than that something failed", async () => {
    fetchProducts.mockRejectedValue(new ApiError(403));

    render(<OutletAssortment />);

    expect(await screen.findByText(/do not have permission/i)).toBeTruthy();
  });

  it("offers no way to change anything to a caller who may only read", async () => {
    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["product:read", "outlet:read"],
    });

    render(<OutletAssortment />);

    const select = await screen.findByLabelText("Assortment for Veridian Still 1L");

    expect((select as HTMLSelectElement).disabled).toBe(true);
    expect(screen.queryByRole("button", { name: "Save overrides" })).toBeNull();
  });

  it("passes on a refusal in the API's own words", async () => {
    setOutletOverrides.mockRejectedValue(
      new ApiError(400, [
        { field: "overrides", message: "A product is added and removed at the same outlet." },
      ]),
    );

    render(<OutletAssortment />);
    await ready();

    await userEvent.selectOptions(
      screen.getByLabelText("Assortment for Veridian Still 1L"),
      "Removed",
    );

    await userEvent.click(screen.getByRole("button", { name: "Save overrides" }));

    expect((await screen.findByRole("alert")).textContent).toContain("added and removed");
  });
});
