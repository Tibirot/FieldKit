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
  contacts: [
    { name: "Ana Ionescu", role: "Buyer", phone: "0721 555 111", email: "ana@example.com" },
    { name: "Bogdan Pop", role: null, phone: null, email: null },
  ],
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

  /** Fills the four required fields and submits. */
  async function submitValid() {
    await userEvent.type(screen.getByLabelText(/^code/i), "OUT-9");
    await userEvent.type(screen.getByLabelText(/outlet name/i), "Corner Shop");
    await userEvent.selectOptions(screen.getByLabelText(/channel/i), "019f-c");
    await userEvent.selectOptions(screen.getByLabelText(/time zone/i), "Europe/Bucharest");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
  }

  it("puts a refusal the server attributed under the control it is about", async () => {
    // The API knows things this form cannot — that a code was taken a second ago, or that a custom
    // field broke a rule only the catalogue holds. Now that it names the field, a server refusal
    // reads exactly like a client-side one instead of appearing in a list somewhere above.
    createOutlet.mockRejectedValue(
      new ApiError(409, [{ field: "code", message: "An outlet with code 'OUT-9' already exists." }]),
    );

    render(<OutletForm />);
    await waitFor(() => expect(fetchChannels).toHaveBeenCalled());
    await submitValid();

    const message = await screen.findByText(/already exists/);
    const code = screen.getByLabelText(/^code/i);

    expect(code.getAttribute("aria-describedby")).toBe(message.id);
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("keeps a refusal it could not attribute at the top", async () => {
    // A message pinned to a guessed control is worse than one that admits it is about the request —
    // and an unknown field is a rule the API grew, not a reason to lose what it said.
    createOutlet.mockRejectedValue(
      new ApiError(409, [{ field: null, message: "That would exceed the tenant's outlet allowance." }]),
    );

    render(<OutletForm />);
    await waitFor(() => expect(fetchChannels).toHaveBeenCalled());
    await submitValid();

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("outlet allowance");
  });

  it("attaches a custom-field refusal despite the API nesting it differently", async () => {
    // The request nests custom fields under `customFields`; the form holds them under `custom`.
    // That one prefix is the only place the two vocabularies disagree.
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
        minimum: null,
        maximum: null,
      },
    ]);

    createOutlet.mockRejectedValue(
      new ApiError(400, [
        { field: "customFields.chillers", message: "'chillers' is not a defined custom field." },
      ]),
    );

    render(<OutletForm />);
    await screen.findByLabelText(/chillers/i);
    await submitValid();

    const message = await screen.findByText(/not a defined custom field/);

    expect(screen.getByLabelText(/chillers/i).getAttribute("aria-describedby")).toBe(message.id);
  });

  it("keeps a custom-field refusal it has no control for at the top", async () => {
    // The catalogue this form built its inputs from can be a moment behind the one the API validated
    // against. `setError` on a path with no control attached swallows the message without a word, so
    // a key the form does not render falls back to the top rather than disappearing.
    createOutlet.mockRejectedValue(
      new ApiError(400, [
        { field: "customFields.freezers", message: "'freezers' is not a defined custom field." },
      ]),
    );

    render(<OutletForm />);
    await waitFor(() => expect(fetchChannels).toHaveBeenCalled());
    await submitValid();

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("not a defined custom field");
  });

  it("sends the contacts it was given back, untouched", async () => {
    // The regression this whole section exists for. Contacts are replaced wholesale, so an absent
    // `contacts` is an emptied one — and this form neither read them nor sent them, which meant
    // fixing a typo in a name silently deleted every person recorded at that outlet. Nothing said
    // so, and the screen never showed them, so nobody would find out until someone went looking.
    render(<OutletForm outlet={OUTLET} />);

    await userEvent.clear(screen.getByLabelText(/outlet name/i));
    await userEvent.type(screen.getByLabelText(/outlet name/i), "Renamed Market");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(updateOutlet).toHaveBeenCalled());

    expect(sent(updateOutlet).contacts).toEqual(OUTLET.contacts);
  });

  it("adds and removes a contact, and what it sends says so", async () => {
    render(<OutletForm outlet={OUTLET} />);

    // Removing the first of two: the row that shifts up must keep its own values. Keyed by index
    // instead of by react-hook-form's row id, React reuses the removed row's input for it and the
    // wrong person appears to have been deleted.
    await userEvent.click(screen.getByRole("button", { name: "Remove contact 1" }));
    await userEvent.click(screen.getByRole("button", { name: "Add contact" }));

    const names = screen.getAllByLabelText(/^name/i);
    await userEvent.type(names[names.length - 1], "Carmen Dinu");

    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(updateOutlet).toHaveBeenCalled());

    expect(sent(updateOutlet).contacts).toEqual([
      { name: "Bogdan Pop", role: null, phone: null, email: null },
      { name: "Carmen Dinu", role: null, phone: null, email: null },
    ]);
  });

  it("will not save a contact with no name", async () => {
    // The one required part of the record: it is what a rep says at the counter. The rest is how to
    // reach the person and may simply not be known yet.
    render(<OutletForm outlet={OUTLET} />);

    await userEvent.click(screen.getByRole("button", { name: "Add contact" }));
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    const names = screen.getAllByLabelText(/^name/i);
    const message = await screen.findByText("This field is required.");

    expect(names[names.length - 1].getAttribute("aria-describedby")).toBe(message.id);
    expect(updateOutlet).not.toHaveBeenCalled();
  });

  it("attaches a refusal about one contact to that contact's control", async () => {
    // `contacts[1].email` is how the request named it; react-hook-form spells the same path
    // `contacts.1.email`. The index is as much of the answer as the field is — a form showing two
    // people cannot work out which of them the server meant.
    updateOutlet.mockRejectedValue(
      new ApiError(400, [
        { field: "contacts[1].email", message: "'0721 555 111' is not an email address." },
      ]),
    );

    render(<OutletForm outlet={OUTLET} />);
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    const message = await screen.findByText(/not an email address/);

    expect(screen.getAllByLabelText(/^email/i)[1].getAttribute("aria-describedby")).toBe(message.id);
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("keeps a refusal about a contact that is not on screen at the top", async () => {
    // An index past the last row is a control that does not exist, and `setError` on a path with no
    // control attached swallows the message without a word.
    updateOutlet.mockRejectedValue(
      new ApiError(400, [{ field: "contacts[7].name", message: "A contact needs a name." }]),
    );

    render(<OutletForm outlet={OUTLET} />);
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    expect((await screen.findByRole("alert")).textContent).toContain("A contact needs a name.");
  });

  it("goes to the outlet it just saved", async () => {
    render(<OutletForm outlet={OUTLET} />);

    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(push).toHaveBeenCalledWith("/outlets/019f-1"));
  });
});

