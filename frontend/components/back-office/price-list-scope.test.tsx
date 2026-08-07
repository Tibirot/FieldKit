// @vitest-environment jsdom

import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { PriceListScope } from "@/components/back-office/price-list-scope";
import type { Channel } from "@/lib/api/channels";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { OutletDetail } from "@/lib/api/outlets";
import type { PriceList, PriceListAssignment } from "@/lib/api/price-lists";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchPriceLists = vi.hoisted(() => vi.fn());
const fetchAssignments = vi.hoisted(() => vi.fn());
const setAssignments = vi.hoisted(() => vi.fn());
const fetchChannels = vi.hoisted(() => vi.fn());
const fetchOutlets = vi.hoisted(() => vi.fn());
const fetchOutlet = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));
vi.mock("next/navigation", () => ({ useParams: () => ({ id: "pl-1" }) }));

vi.mock("@/lib/api/price-lists", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/price-lists")>()),
  fetchPriceLists: (...args: unknown[]) => fetchPriceLists(...args),
  fetchAssignments: (...args: unknown[]) => fetchAssignments(...args),
  setAssignments: (...args: unknown[]) => setAssignments(...args),
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

/**
 * Waits for the permission answer.
 *
 * `usePermissions` lives in the editor, whose identity query starts a tick after the first paint —
 * asserting before it lands finds controls that are still disabled, and a `userEvent.click` on a
 * disabled control passes by doing nothing.
 */
async function ready(): Promise<void> {
  await screen.findByRole("button", { name: "Save scope" });
}

const LIST: PriceList = {
  id: "pl-1",
  name: "Modern Trade 2026",
  currency: "EUR",
  effectiveFrom: "2026-01-01",
  effectiveTo: null,
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

const ASSIGNED: PriceListAssignment[] = [
  { channelId: "ch-mt", outletId: null },
  { channelId: null, outletId: "out-1" },
];

describe("<PriceListScope>", () => {
  beforeEach(() => {
    fetchPriceLists.mockReset().mockResolvedValue([LIST]);
    fetchAssignments.mockReset().mockResolvedValue(ASSIGNED);
    setAssignments.mockReset().mockResolvedValue(ASSIGNED);
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

  it("shows the channels a list already reaches as ticked", async () => {
    render(<PriceListScope />);
    await ready();

    expect((screen.getByLabelText("Assign to Modern Trade") as HTMLInputElement).checked).toBe(true);
    expect((screen.getByLabelText("Assign to Traditional Trade") as HTMLInputElement).checked).toBe(
      false,
    );
  });

  it("names an already-assigned outlet rather than showing its id", async () => {
    // The assignment carries an id and nothing else; a screen that showed it raw would be asking an
    // author to recognise a GUID.
    render(<PriceListScope />);
    await ready();

    expect(screen.getByText("Veridian Central")).toBeTruthy();
    expect(screen.getByText("RO-0001")).toBeTruthy();
  });

  it("keeps an outlet whose name could not be loaded, rather than dropping it on save", async () => {
    // The row exists server-side regardless of whether this screen could read the outlet. Hiding it
    // would remove it from the next PUT — a silent unassignment caused by a failed GET.
    fetchOutlet.mockRejectedValue(new ApiError(403));

    render(<PriceListScope />);
    await ready();

    expect(screen.getByText(/could not be loaded/i)).toBeTruthy();

    await userEvent.click(screen.getByLabelText("Assign to Traditional Trade"));
    await userEvent.click(screen.getByRole("button", { name: "Save scope" }));

    await waitFor(() => expect(setAssignments).toHaveBeenCalled());
    expect(setAssignments.mock.calls[0][2].outletIds).toEqual(["out-1"]);
  });

  it("sends both scopes together, because the PUT replaces the whole thing", async () => {
    render(<PriceListScope />);
    await ready();

    await userEvent.click(screen.getByLabelText("Assign to Traditional Trade"));
    await userEvent.click(screen.getByRole("button", { name: "Save scope" }));

    await waitFor(() => expect(setAssignments).toHaveBeenCalled());

    const sent = setAssignments.mock.calls[0][2];

    expect(sent.channelIds).toEqual(expect.arrayContaining(["ch-mt", "ch-tt"]));
    expect(sent.channelIds).toHaveLength(2);
    expect(sent.outletIds).toEqual(["out-1"]);
  });

  it("lets a list be withdrawn by emptying its scope", async () => {
    // An empty scope is a real decision, not a mistake to guard against: it is how a list stops
    // applying, and the server announces it so devices holding the list hear that it moved.
    render(<PriceListScope />);
    await ready();

    await userEvent.click(screen.getByLabelText("Assign to Modern Trade"));
    await userEvent.click(screen.getByRole("button", { name: "Remove Veridian Central (RO-0001)" }));

    expect(screen.getByRole("status").textContent).toMatch(/reaches nobody/i);

    await userEvent.click(screen.getByRole("button", { name: "Save scope" }));

    await waitFor(() => expect(setAssignments).toHaveBeenCalled());
    expect(setAssignments.mock.calls[0][2]).toEqual({ channelIds: [], outletIds: [] });
  });

  it("searches outlets on the server, and not before two characters", async () => {
    // A client-side filter would search the one page it happens to hold and look like it searched
    // the base. One character would ask the server for a fifth of the tenant's outlets.
    render(<PriceListScope />);
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

    render(<PriceListScope />);
    await ready();

    await userEvent.type(screen.getByLabelText("Search outlets"), "north");
    await userEvent.click(await screen.findByRole("button", { name: "Add Veridian North (RO-0002)" }));

    await userEvent.click(screen.getByRole("button", { name: "Save scope" }));

    await waitFor(() => expect(setAssignments).toHaveBeenCalled());
    expect(setAssignments.mock.calls[0][2].outletIds).toEqual(["out-1", "out-2"]);
  });

  it("cannot add the same outlet twice", async () => {
    // Adding it again would silently do nothing, and the PUT would carry it once either way. Saying
    // so on the button is the difference between a no-op and a broken one.
    fetchOutlets.mockResolvedValue({ items: [CENTRAL], total: 1, page: 1, pageSize: 10 });

    render(<PriceListScope />);
    await ready();

    await userEvent.type(screen.getByLabelText("Search outlets"), "central");

    const add = await screen.findByRole("button", { name: "Add Veridian Central (RO-0001)" });
    expect((add as HTMLButtonElement).disabled).toBe(true);
  });

  it("offers nothing to save until the scope changes", async () => {
    render(<PriceListScope />);

    const save = await screen.findByRole("button", { name: "Save scope" });
    expect((save as HTMLButtonElement).disabled).toBe(true);

    await userEvent.click(screen.getByLabelText("Assign to Traditional Trade"));
    expect((save as HTMLButtonElement).disabled).toBe(false);

    await userEvent.click(screen.getByLabelText("Assign to Traditional Trade"));
    expect((save as HTMLButtonElement).disabled).toBe(true);
  });

  it("counts removing an outlet as a change, not only adding one", async () => {
    // A removal leaves nothing new behind to compare against, so a dirty check that only looks for
    // unfamiliar entries misses it — and taking a shop off an override would silently not save.
    render(<PriceListScope />);

    const save = await screen.findByRole("button", { name: "Save scope" });
    expect((save as HTMLButtonElement).disabled).toBe(true);

    await userEvent.click(screen.getByRole("button", { name: "Remove Veridian Central (RO-0001)" }));
    expect((save as HTMLButtonElement).disabled).toBe(false);
  });

  it("counts every place the list reaches, channels and outlets alike", async () => {
    render(<PriceListScope />);
    await ready();

    expect(screen.getByRole("status").textContent).toContain("2 places");
  });

  it("says a price list that does not exist is missing rather than broken", async () => {
    fetchPriceLists.mockResolvedValue([]);

    render(<PriceListScope />);

    expect(await screen.findByText(/does not exist/i)).toBeTruthy();
  });

  it("says which permission is missing rather than that something failed", async () => {
    fetchAssignments.mockRejectedValue(new ApiError(403));

    render(<PriceListScope />);

    expect(await screen.findByText(/do not have permission/i)).toBeTruthy();
  });

  it("offers no way to change anything to a caller who may only read", async () => {
    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["product:read", "channel:read"],
    });

    render(<PriceListScope />);

    const box = await screen.findByLabelText("Assign to Modern Trade");

    await waitFor(() => expect((box as HTMLInputElement).disabled).toBe(true));
    expect(screen.queryByRole("button", { name: "Save scope" })).toBeNull();
    expect(screen.queryByLabelText("Search outlets")).toBeNull();
    expect(screen.queryByRole("button", { name: "Remove Veridian Central (RO-0001)" })).toBeNull();

    // Still readable: which shops pay these prices is the question a reader came to answer.
    expect((box as HTMLInputElement).checked).toBe(true);
    expect(screen.getByText("Veridian Central")).toBeTruthy();
  });

  it("passes on a refusal in the API's own words", async () => {
    setAssignments.mockRejectedValue(
      new ApiError(400, [{ field: "outletIds", message: "1 outlet(s) do not exist." }]),
    );

    render(<PriceListScope />);
    await ready();

    await userEvent.click(screen.getByLabelText("Assign to Traditional Trade"));
    await userEvent.click(screen.getByRole("button", { name: "Save scope" }));

    expect((await screen.findByRole("alert")).textContent).toContain("do not exist");
  });

  it("says a tenant with no channels has none, rather than showing an empty box", async () => {
    fetchChannels.mockResolvedValue([]);
    fetchAssignments.mockResolvedValue([]);

    render(<PriceListScope />);
    await ready();

    const channels = screen.getByRole("heading", { name: "Channels" }).parentElement!;

    expect(within(channels).getByText(/No channels yet/i)).toBeTruthy();
  });
});
