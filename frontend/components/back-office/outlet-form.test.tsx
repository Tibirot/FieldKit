// @vitest-environment jsdom

import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { OutletForm } from "@/components/back-office/outlet-form";
import { ApiError } from "@/lib/api/client";
import type { OutletDetail } from "@/lib/api/outlets";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const push = vi.hoisted(() => vi.fn());
const createOutlet = vi.hoisted(() => vi.fn());
const updateOutlet = vi.hoisted(() => vi.fn());
const fetchChannels = vi.hoisted(() => vi.fn());
const fetchFieldDefinitions = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));
vi.mock("@/i18n/navigation", () => ({ useRouter: () => ({ push }) }));

vi.mock("@/lib/api/outlets", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/outlets")>()),
  createOutlet: (...args: unknown[]) => createOutlet(...args),
  updateOutlet: (...args: unknown[]) => updateOutlet(...args),
}));

vi.mock("@/lib/api/channels", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/channels")>()),
  fetchChannels: (...args: unknown[]) => fetchChannels(...args),
}));

vi.mock("@/lib/api/field-definitions", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/field-definitions")>()),
  fetchFieldDefinitions: (...args: unknown[]) => fetchFieldDefinitions(...args),
}));

const OUTLET: OutletDetail = {
  id: "019f-1",
  code: "OUT-2214",
  name: "Select Market Dorobanți",
  channelId: "019f-c",
  channelName: "Modern Trade",
  segment: "A",
  banner: null,
  status: "Active",
  territory: null,
  timeZoneId: "Europe/Bucharest",
  address: { street: "Str. Dorobanți 1", city: "Bucharest", postalCode: "010001", countryCode: "RO" },
  location: { latitude: 44.4682, longitude: 26.0921 },
  customFields: { chillers: 4 },
};

/** What the last save was asked to send. */
const sent = (spy: typeof createOutlet) => spy.mock.calls.at(-1)?.at(-1) as Record<string, unknown>;

describe("<OutletForm>", () => {
  beforeEach(() => {
    push.mockReset();
    createOutlet.mockReset().mockResolvedValue(OUTLET);
    updateOutlet.mockReset().mockResolvedValue(OUTLET);
    fetchChannels.mockReset().mockResolvedValue([{ id: "019f-c", name: "Modern Trade" }]);
    fetchFieldDefinitions.mockReset().mockResolvedValue([]);

    auth.current = {
      status: "authenticated",
      user: { access_token: "token", profile: { sub: "subject-a" } },
      workspace: "fieldkit-dev",
      signIn: vi.fn(),
      signOut: vi.fn(),
      completeSignIn: vi.fn(),
    } as unknown as AuthContextValue;
  });

  it("takes a code when creating, and refuses to change one when editing", async () => {
    // The code is the identifier every territory membership and import file already refers to, so
    // `Outlet.Update` has no parameter for it. A form that let someone type into it would be
    // offering an edit the server will not make.
    const creating = render(<OutletForm />);
    expect((screen.getByLabelText(/^code/i) as HTMLInputElement).readOnly).toBe(false);
    creating.unmount();

    render(<OutletForm outlet={OUTLET} />);
    const code = screen.getByLabelText(/^code/i) as HTMLInputElement;

    expect(code.readOnly).toBe(true);
    expect(code.value).toBe("OUT-2214");
  });

  it("sends what was typed, and nothing it was not given", async () => {
    render(<OutletForm />);
    await waitFor(() => expect(fetchChannels).toHaveBeenCalled());

    await userEvent.type(screen.getByLabelText(/^code/i), "OUT-9");
    await userEvent.type(screen.getByLabelText(/outlet name/i), "Corner Shop");
    await userEvent.selectOptions(screen.getByLabelText(/channel/i), "019f-c");
    await userEvent.selectOptions(screen.getByLabelText(/time zone/i), "Europe/Bucharest");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(createOutlet).toHaveBeenCalled());

    expect(sent(createOutlet)).toMatchObject({
      code: "OUT-9",
      name: "Corner Shop",
      channelId: "019f-c",
      timeZoneId: "Europe/Bucharest",
    });

    // An address of four nulls is not an address, and half a coordinate is a mistake the server
    // would refuse — so neither is sent at all.
    expect(sent(createOutlet).address).toBeNull();
    expect(sent(createOutlet).location).toBeNull();
  });

  it("sends coordinates only when both halves are there", async () => {
    render(<OutletForm />);
    await waitFor(() => expect(fetchChannels).toHaveBeenCalled());

    await userEvent.type(screen.getByLabelText(/^code/i), "OUT-9");
    await userEvent.type(screen.getByLabelText(/outlet name/i), "Corner Shop");
    await userEvent.selectOptions(screen.getByLabelText(/channel/i), "019f-c");
    await userEvent.selectOptions(screen.getByLabelText(/time zone/i), "Europe/Bucharest");
    await userEvent.type(screen.getByLabelText(/latitude/i), "44.4682");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(createOutlet).toHaveBeenCalled());
    expect(sent(createOutlet).location).toBeNull();
  });

  it("lets the browser refuse a latitude that is not a place", () => {
    // The same bounds the shared GeoPoint enforces, so the refusal happens before a request.
    render(<OutletForm />);

    const latitude = screen.getByLabelText(/latitude/i) as HTMLInputElement;

    expect(latitude.min).toBe("-90");
    expect(latitude.max).toBe("90");
  });

  it("carries the tenant's own fields through a save", async () => {
    fetchFieldDefinitions.mockResolvedValue([
      {
        id: "f1",
        entity: "Outlet",
        key: "chillers",
        label: "Chillers",
        type: "Number",
        required: false,
        options: [],
        maxLength: null,
        minimum: 0,
        maximum: 50,
      },
    ]);

    render(<OutletForm outlet={OUTLET} />);

    const chillers = await screen.findByLabelText(/chillers/i);
    expect((chillers as HTMLInputElement).value).toBe("4");

    await userEvent.clear(chillers);
    expect((chillers as HTMLInputElement).value).toBe("");

    await userEvent.type(chillers, "7");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(updateOutlet).toHaveBeenCalled());
    expect(sent(updateOutlet).customFields).toEqual({ chillers: 7 });
  });

  it("repeats what the server refused, in the server's words", async () => {
    // The API knows things this form cannot — that a code was taken a second ago, or that a custom
    // field broke a rule only the catalogue holds. Replacing those with "something went wrong"
    // throws away the only description of what is actually wrong.
    createOutlet.mockRejectedValue(
      new ApiError(409, ["An outlet with code 'OUT-9' already exists."]),
    );

    render(<OutletForm />);
    await waitFor(() => expect(fetchChannels).toHaveBeenCalled());

    await userEvent.type(screen.getByLabelText(/^code/i), "OUT-9");
    await userEvent.type(screen.getByLabelText(/outlet name/i), "Corner Shop");
    await userEvent.selectOptions(screen.getByLabelText(/channel/i), "019f-c");
    await userEvent.selectOptions(screen.getByLabelText(/time zone/i), "Europe/Bucharest");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("already exists");
  });

  it("goes to the outlet it just saved", async () => {
    render(<OutletForm outlet={OUTLET} />);

    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(push).toHaveBeenCalledWith("/outlets/019f-1"));
  });
});
