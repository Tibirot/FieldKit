// @vitest-environment jsdom

import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { PromotionScope } from "@/components/back-office/promotion-scope";
import type { Channel } from "@/lib/api/channels";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { OutletDetail } from "@/lib/api/outlets";
import type { Promotion, PromotionAssignment } from "@/lib/api/promotions";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchPromotions = vi.hoisted(() => vi.fn());
const fetchPromotionScope = vi.hoisted(() => vi.fn());
const setPromotionScope = vi.hoisted(() => vi.fn());
const fetchChannels = vi.hoisted(() => vi.fn());
const fetchOutlets = vi.hoisted(() => vi.fn());
const fetchOutlet = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));
vi.mock("next/navigation", () => ({ useParams: () => ({ id: "promo-1" }) }));

vi.mock("@/lib/api/promotions", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/promotions")>()),
  fetchPromotions: (...args: unknown[]) => fetchPromotions(...args),
  fetchPromotionScope: (...args: unknown[]) => fetchPromotionScope(...args),
  setPromotionScope: (...args: unknown[]) => setPromotionScope(...args),
}));

vi.mock("@/lib/api/channels", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/channels")>()),
  fetchChannels: (...args: unknown[]) => fetchChannels(...args),
}));

vi.mock("@/lib/api/outlets", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/outlets")>()),
  fetchOutlets: (...args: unknown[]) => fetchOutlets(...args),
  fetchOutlet: (...args: unknown[]) => fetchOutlet(...args),
}));

/** Waits for the permission answer — see the note in outlet-assortment.test.tsx. */
async function ready(): Promise<void> {
  await screen.findByRole("button", { name: "Save scope" });
}

const PROMOTION: Promotion = {
  id: "promo-1",
  name: "Summer refresh",
  type: "PercentOff",
  value: "10.00",
  currency: null,
  validFrom: "2026-06-01",
  validTo: null,
  priority: 10,
  bundle: null,
};

const CHANNELS: Channel[] = [
  { id: "ch-mt", name: "Modern Trade" },
  { id: "ch-tt", name: "Traditional Trade" },
];

function outlet(id: string, code: string, name: string): OutletDetail {
  return {
    id,
    code,
    name,
    channelId: "ch-mt",
    channelName: "Modern Trade",
    segment: null,
    banner: null,
    status: "Active",
    territory: null,
    timeZoneId: "Europe/Bucharest",
    address: null,
    location: null,
    contacts: [],
    customFields: {},
  };
}

const CENTRAL = outlet("out-1", "RO-0001", "Veridian Central");
const NORTH = outlet("out-2", "RO-0002", "Veridian North");

const ASSIGNED: PromotionAssignment[] = [
  { channelId: "ch-mt", outletId: null },
  { channelId: null, outletId: "out-1" },
];

describe("<PromotionScope>", () => {
  beforeEach(() => {
    fetchPromotions.mockReset().mockResolvedValue([PROMOTION]);
    fetchPromotionScope.mockReset().mockResolvedValue(ASSIGNED);
    setPromotionScope.mockReset().mockResolvedValue(ASSIGNED);
    fetchChannels.mockReset().mockResolvedValue(CHANNELS);
    fetchOutlets.mockReset().mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 10 });
    fetchOutlet.mockReset().mockResolvedValue(CENTRAL);

    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["product:read", "product:write", "outlet:read", "channel:read"],
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

  it("shows the channels a promotion already runs in as ticked", async () => {
    render(<PromotionScope />);
    await ready();

    expect((screen.getByLabelText("Run in Modern Trade") as HTMLInputElement).checked).toBe(true);
    expect((screen.getByLabelText("Run in Traditional Trade") as HTMLInputElement).checked).toBe(
      false,
    );
  });

  it("names an already-assigned outlet rather than showing its id", async () => {
    render(<PromotionScope />);
    await ready();

    expect(screen.getByText("Veridian Central")).toBeTruthy();
    expect(screen.getByText("RO-0001")).toBeTruthy();
  });

  it("keeps an outlet whose name could not be loaded, rather than dropping it on save", async () => {
    // The row exists server-side regardless of whether this screen could read the outlet. Hiding it
    // would remove it from the next PUT — a silent unassignment caused by a failed GET.
    fetchOutlet.mockRejectedValue(new ApiError(403));

    render(<PromotionScope />);
    await ready();

    expect(screen.getByText(/could not be loaded/i)).toBeTruthy();

    await userEvent.click(screen.getByLabelText("Run in Traditional Trade"));
    await userEvent.click(screen.getByRole("button", { name: "Save scope" }));

    await waitFor(() => expect(setPromotionScope).toHaveBeenCalled());
    expect(setPromotionScope.mock.calls[0][2].outletIds).toEqual(["out-1"]);
  });

  it("sends both scopes together, because the PUT replaces the whole thing", async () => {
    render(<PromotionScope />);
    await ready();

    await userEvent.click(screen.getByLabelText("Run in Traditional Trade"));
    await userEvent.click(screen.getByRole("button", { name: "Save scope" }));

    await waitFor(() => expect(setPromotionScope).toHaveBeenCalled());

    const sent = setPromotionScope.mock.calls[0][2];

    expect(sent.channelIds).toEqual(expect.arrayContaining(["ch-mt", "ch-tt"]));
    expect(sent.channelIds).toHaveLength(2);
    expect(sent.outletIds).toEqual(["out-1"]);
  });

  it("lets a promotion be taken out of play by emptying its scope", async () => {
    // An empty scope is a real decision: the deal stops running without its window being edited or
    // a record other things point at being deleted, and the server announces it either way.
    render(<PromotionScope />);
    await ready();

    await userEvent.click(screen.getByLabelText("Run in Modern Trade"));
    await userEvent.click(screen.getByRole("button", { name: "Remove Veridian Central (RO-0001)" }));

    expect(screen.getByRole("status").textContent).toMatch(/runs nowhere/i);

    await userEvent.click(screen.getByRole("button", { name: "Save scope" }));

    await waitFor(() => expect(setPromotionScope).toHaveBeenCalled());
    expect(setPromotionScope.mock.calls[0][2]).toEqual({ channelIds: [], outletIds: [] });
  });

  it("searches outlets on the server, and not before two characters", async () => {
    render(<PromotionScope />);
    await ready();

    await userEvent.type(screen.getByLabelText("Search outlets"), "n");
    expect(fetchOutlets).not.toHaveBeenCalled();

    fetchOutlets.mockResolvedValue({ items: [NORTH], total: 1, page: 1, pageSize: 10 });
    await userEvent.type(screen.getByLabelText("Search outlets"), "o");

    await waitFor(() => expect(fetchOutlets).toHaveBeenCalled());
    expect(fetchOutlets.mock.calls[0][1]).toMatchObject({ search: "no" });
    expect(await screen.findByRole("button", { name: "Add Veridian North (RO-0002)" })).toBeTruthy();
  });

  it("adds a searched outlet to the scope", async () => {
    fetchOutlets.mockResolvedValue({ items: [NORTH], total: 1, page: 1, pageSize: 10 });

    render(<PromotionScope />);
    await ready();

    await userEvent.type(screen.getByLabelText("Search outlets"), "north");
    await userEvent.click(await screen.findByRole("button", { name: "Add Veridian North (RO-0002)" }));
    await userEvent.click(screen.getByRole("button", { name: "Save scope" }));

    await waitFor(() => expect(setPromotionScope).toHaveBeenCalled());
    expect(setPromotionScope.mock.calls[0][2].outletIds).toEqual(["out-1", "out-2"]);
  });

  it("cannot add the same outlet twice", async () => {
    fetchOutlets.mockResolvedValue({ items: [CENTRAL], total: 1, page: 1, pageSize: 10 });

    render(<PromotionScope />);
    await ready();

    await userEvent.type(screen.getByLabelText("Search outlets"), "central");

    const add = await screen.findByRole("button", { name: "Add Veridian Central (RO-0001)" });
    expect((add as HTMLButtonElement).disabled).toBe(true);
  });

  it("counts removing an outlet as a change, not only adding one", async () => {
    render(<PromotionScope />);

    const save = await screen.findByRole("button", { name: "Save scope" });
    expect((save as HTMLButtonElement).disabled).toBe(true);

    await userEvent.click(screen.getByRole("button", { name: "Remove Veridian Central (RO-0001)" }));
    expect((save as HTMLButtonElement).disabled).toBe(false);
  });

  it("offers nothing to save until the scope changes", async () => {
    render(<PromotionScope />);

    const save = await screen.findByRole("button", { name: "Save scope" });
    expect((save as HTMLButtonElement).disabled).toBe(true);

    await userEvent.click(screen.getByLabelText("Run in Traditional Trade"));
    expect((save as HTMLButtonElement).disabled).toBe(false);

    await userEvent.click(screen.getByLabelText("Run in Traditional Trade"));
    expect((save as HTMLButtonElement).disabled).toBe(true);
  });

  it("counts every place the promotion runs, channels and outlets alike", async () => {
    render(<PromotionScope />);
    await ready();

    expect(screen.getByRole("status").textContent).toContain("2 places");
  });

  it("says a promotion that does not exist is missing rather than broken", async () => {
    fetchPromotions.mockResolvedValue([]);

    render(<PromotionScope />);

    expect(await screen.findByText(/does not exist/i)).toBeTruthy();
  });

  it("says which permission is missing rather than that something failed", async () => {
    fetchPromotionScope.mockRejectedValue(new ApiError(403));

    render(<PromotionScope />);

    expect(await screen.findByText(/do not have permission/i)).toBeTruthy();
  });

  it("offers no way to change anything to a caller who may only read", async () => {
    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["product:read", "channel:read"],
    });

    render(<PromotionScope />);

    const box = await screen.findByLabelText("Run in Modern Trade");

    await waitFor(() => expect((box as HTMLInputElement).disabled).toBe(true));
    expect(screen.queryByRole("button", { name: "Save scope" })).toBeNull();
    expect(screen.queryByLabelText("Search outlets")).toBeNull();
    expect(screen.queryByRole("button", { name: "Remove Veridian Central (RO-0001)" })).toBeNull();

    // Still readable: where a deal runs is the question a reader came to answer.
    expect((box as HTMLInputElement).checked).toBe(true);
    expect(screen.getByText("Veridian Central")).toBeTruthy();
  });

  it("passes on a refusal in the API's own words", async () => {
    setPromotionScope.mockRejectedValue(
      new ApiError(400, [{ field: "outletIds", message: "1 outlet(s) do not exist." }]),
    );

    render(<PromotionScope />);
    await ready();

    await userEvent.click(screen.getByLabelText("Run in Traditional Trade"));
    await userEvent.click(screen.getByRole("button", { name: "Save scope" }));

    expect((await screen.findByRole("alert")).textContent).toContain("do not exist");
  });

  it("says a tenant with no channels has none, rather than showing an empty box", async () => {
    fetchChannels.mockResolvedValue([]);
    fetchPromotionScope.mockResolvedValue([]);

    render(<PromotionScope />);
    await ready();

    const channels = screen.getByRole("heading", { name: "Channels" }).parentElement!;

    expect(within(channels).getByText(/No channels yet/i)).toBeTruthy();
  });
});
