// @vitest-environment jsdom

import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { ChannelBrowser } from "@/components/back-office/channel-browser";
import type { Channel } from "@/lib/api/channels";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchChannels = vi.hoisted(() => vi.fn());
const createChannel = vi.hoisted(() => vi.fn());
const updateChannel = vi.hoisted(() => vi.fn());
const deleteChannel = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

vi.mock("@/lib/api/channels", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/channels")>()),
  fetchChannels: (...args: unknown[]) => fetchChannels(...args),
  createChannel: (...args: unknown[]) => createChannel(...args),
  updateChannel: (...args: unknown[]) => updateChannel(...args),
  deleteChannel: (...args: unknown[]) => deleteChannel(...args),
}));

const CHANNELS: Channel[] = [
  { id: "c-mt", name: "Modern Trade" },
  { id: "c-ho", name: "HoReCa" },
];

describe("<ChannelBrowser>", () => {
  beforeEach(() => {
    fetchChannels.mockReset().mockResolvedValue(CHANNELS);
    createChannel.mockReset().mockResolvedValue(CHANNELS[0]);
    updateChannel.mockReset().mockResolvedValue(CHANNELS[0]);
    deleteChannel.mockReset().mockResolvedValue(undefined);

    auth.current = {
      status: "authenticated",
      user: { access_token: "token", profile: { sub: "subject-a" } },
      workspace: "fieldkit-dev",
      signIn: vi.fn(),
      signOut: vi.fn(),
      completeSignIn: vi.fn(),
    } as unknown as AuthContextValue;
  });

  it("tells an empty workspace that outlets depend on this", async () => {
    // The state that made this screen necessary. Before it, the outlet form's channel dropdown was
    // empty with no way to fill it, and nothing on any screen said why.
    fetchChannels.mockResolvedValue([]);

    render(<ChannelBrowser />);

    expect(await screen.findByText(/an outlet cannot be created without one/)).toBeTruthy();
  });

  it("lists the classifications a tenant works with", async () => {
    render(<ChannelBrowser />);

    const items = await screen.findAllByRole("listitem");

    expect(items.map((item) => item.textContent?.split("Rename")[0])).toEqual([
      "Modern Trade",
      "HoReCa",
    ]);
  });

  it("creates one", async () => {
    render(<ChannelBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "New channel" }));
    await userEvent.type(screen.getByLabelText(/^name/i), "Traditional Trade");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(createChannel).toHaveBeenCalled());

    expect(createChannel).toHaveBeenCalledWith("token", { name: "Traditional Trade" });
  });

  it("opens the form on the channel whose Rename was pressed", async () => {
    // React Hook Form captures its defaults on the first render, so without a key per target the
    // second channel's form would show the first one's name.
    render(<ChannelBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "Rename Modern Trade" }));
    expect((screen.getByLabelText(/^name/i) as HTMLInputElement).value).toBe("Modern Trade");

    await userEvent.click(screen.getByRole("button", { name: "Rename HoReCa" }));
    expect((screen.getByLabelText(/^name/i) as HTMLInputElement).value).toBe("HoReCa");
  });

  it("puts a name the server refused under the name box", async () => {
    // Unique per tenant over `lower(name)`, so a rename that collides is the ordinary case.
    createChannel.mockRejectedValue(
      new ApiError(409, [{ field: "name", message: "A channel named 'HoReCa' already exists." }]),
    );

    render(<ChannelBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "New channel" }));
    await userEvent.type(screen.getByLabelText(/^name/i), "horeca");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    const message = await screen.findByText(/already exists/);

    expect(screen.getByLabelText(/^name/i).getAttribute("aria-describedby")).toBe(message.id);
  });

  it("shows what the server said when outlets still use a channel", async () => {
    // BR-OUT-1: every outlet has a channel, so there is no removing one from underneath the outlets
    // filed under it. The count and the next step are the useful part.
    deleteChannel.mockRejectedValue(
      new ApiError(409, [
        { field: null, message: "12 outlet(s) are classified as 'Modern Trade'. Reclassify them first." },
      ]),
    );

    render(<ChannelBrowser />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "Delete Modern Trade" }));

    expect((await screen.findByRole("alert")).textContent).toContain("12 outlet(s)");
  });

  it("offers no way to change anything to a caller who may only read", async () => {
    // `channel:write` is what the importer pointedly does not hold, so a typo in one cell cannot
    // mint a permanent classification. Someone may maintain outlets without inventing the vocabulary
    // they are filed under.
    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["channel:read", "outlet:read", "outlet:write"],
    });

    render(<ChannelBrowser />);
    await screen.findAllByRole("listitem");

    expect(screen.queryByRole("button", { name: "New channel" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Rename Modern Trade" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Delete Modern Trade" })).toBeNull();
    expect(screen.getByText("Modern Trade")).toBeTruthy();
  });
});
