// @vitest-environment jsdom

import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { OutletForm } from "@/components/back-office/outlet-form";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
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

    // Restated per test, because the form now gates on `outlet:write` and the one test that narrows
    // the caller would otherwise leave every test after it looking at a read-only form.
    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["outlet:read", "outlet:write", "channel:read", "config:read"],
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

  it("shows the channel the outlet is actually in, before the channel list arrives", async () => {
    // Found in the browser: an outlet in "MT 8839" displayed "HoReCa 2001", because the select is
    // registered uncontrolled and React Hook Form assigns `select.value` on mount. Until the channel
    // list resolves there is no matching <option>, so that assignment silently does nothing and the
    // browser settles on whichever channel renders first.
    //
    // A form that shows a shop in the wrong channel is not a cosmetic problem: channel drives
    // assortment, pricing and the visit workflow, and someone reading it would believe it.
    //
    // The list never resolves here, which is the sharpest version of "has not resolved yet".
    fetchChannels.mockReturnValue(new Promise(() => {}));

    render(<OutletForm outlet={OUTLET} />);

    const channel = screen.getByLabelText(/channel/i) as HTMLSelectElement;

    expect(channel.value).toBe("019f-c");
    expect(channel.options[channel.selectedIndex].textContent).toBe("Modern Trade");
  });

  it("does not offer the stored channel twice once the list arrives", async () => {
    // The stored channel is prepended only when the loaded list lacks it. Otherwise an outlet would
    // see its own channel listed once from the fallback and once from the server.
    render(<OutletForm outlet={OUTLET} />);
    await waitFor(() => expect(fetchChannels).toHaveBeenCalled());

    await waitFor(() => {
      const channel = screen.getByLabelText(/channel/i) as HTMLSelectElement;
      const named = Array.from(channel.options).filter((o) => o.value === "019f-c");

      expect(named).toHaveLength(1);
    });
  });

  it("sends what was typed, and nothing it was not given", async () => {
    render(<OutletForm />);
    await waitFor(() => expect(fetchChannels).toHaveBeenCalled());

    await userEvent.type(screen.getByLabelText(/^code/i), "OUT-9");
    await userEvent.type(screen.getByLabelText(/outlet name/i), "Corner Shop");
    await userEvent.selectOptions(screen.getByLabelText(/channel/i), "019f-c");
    await userEvent.selectOptions(screen.getByLabelText(/time zone/i), "Europe/Bucharest");
    await userEvent.click(await screen.findByRole("button", { name: "Save" }));

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
    await userEvent.click(await screen.findByRole("button", { name: "Save" }));

    await waitFor(() => expect(createOutlet).toHaveBeenCalled());
    expect(sent(createOutlet).location).toBeNull();
  });

  it("refuses a country that was spelled out, rather than clipping it to two letters", async () => {
    // Found in the browser, not here. The input carried `maxLength={2}`, so typing "Bulgaria" left
    // "Bu" in the box — the server upper-cased that to a perfectly well-formed "BU" and stored it,
    // and the outlet sat in a country that does not exist. Bulgaria is BG. Nothing downstream could
    // catch it: by the time the request was sent the value *was* two letters, so the server's own
    // two-letter rule passed. The truncation had to stop happening for the check to mean anything.
    render(<OutletForm />);
    await waitFor(() => expect(fetchChannels).toHaveBeenCalled());

    await userEvent.type(screen.getByLabelText(/^code/i), "OUT-9");
    await userEvent.type(screen.getByLabelText(/outlet name/i), "Corner Shop");
    await userEvent.selectOptions(screen.getByLabelText(/channel/i), "019f-c");
    await userEvent.selectOptions(screen.getByLabelText(/time zone/i), "Europe/Bucharest");
    await userEvent.type(screen.getByLabelText(/country code/i), "Bulgaria");

    // The whole word survives the typing — this is the assertion that fails on `maxLength={2}`.
    expect((screen.getByLabelText(/country code/i) as HTMLInputElement).value).toBe("Bulgaria");

    await userEvent.click(await screen.findByRole("button", { name: "Save" }));

    expect(await screen.findByText(/two-letter country code/i)).toBeTruthy();
    expect(createOutlet).not.toHaveBeenCalled();
  });

  it("still takes a country that is a country", async () => {
    render(<OutletForm />);
    await waitFor(() => expect(fetchChannels).toHaveBeenCalled());

    await userEvent.type(screen.getByLabelText(/^code/i), "OUT-9");
    await userEvent.type(screen.getByLabelText(/outlet name/i), "Corner Shop");
    await userEvent.selectOptions(screen.getByLabelText(/channel/i), "019f-c");
    await userEvent.selectOptions(screen.getByLabelText(/time zone/i), "Europe/Bucharest");

    // Lower case on purpose: the server upper-cases it, and refusing it here would make the form
    // stricter than the API for no reason a user could guess.
    await userEvent.type(screen.getByLabelText(/country code/i), "ro");
    await userEvent.click(await screen.findByRole("button", { name: "Save" }));

    await waitFor(() => expect(createOutlet).toHaveBeenCalled());
    expect(sent(createOutlet).address).toMatchObject({ countryCode: "ro" });
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
    await userEvent.click(await screen.findByRole("button", { name: "Save" }));

    await waitFor(() => expect(updateOutlet).toHaveBeenCalled());
    expect(sent(updateOutlet).customFields).toEqual({ chillers: 7 });
  });

  /** Fills the four required fields and submits. */
  async function submitValid() {
    await userEvent.type(screen.getByLabelText(/^code/i), "OUT-9");
    await userEvent.type(screen.getByLabelText(/outlet name/i), "Corner Shop");
    await userEvent.selectOptions(screen.getByLabelText(/channel/i), "019f-c");
    await userEvent.selectOptions(screen.getByLabelText(/time zone/i), "Europe/Bucharest");

    // `findBy`, not `getBy`. The form gates Save on `outlet:write`, and `usePermissions` counts a
    // pending answer as denied — so Save is genuinely absent for the tick before the identity query
    // lands. A `getBy` here would fail on a form that is about to work, and a test that clicked
    // nothing would pass by asserting nothing.
    await userEvent.click(await screen.findByRole("button", { name: "Save" }));
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

  it("says something when the API refuses without saying why", async () => {
    // The regression. A 403, a 404, or a 500 with no body all arrive as `ApiError` with an empty
    // `problems`, so the loop that routes problems to controls ran zero times and the fallback sat
    // on the branch above it — the screen went completely silent and a Save button looked broken.
    // Found by clicking Save as a reader with `outlet:read` and watching nothing happen.
    createOutlet.mockRejectedValue(new ApiError(403));

    render(<OutletForm />);
    await waitFor(() => expect(fetchChannels).toHaveBeenCalled());
    await submitValid();

    expect((await screen.findByRole("alert")).textContent).toBeTruthy();
  });

  it("offers no way to save to a caller who may only read outlets", async () => {
    // Every Products screen gates its own writes on `product:write`; this one gated nothing, so a
    // reader got a filled-in form and a Save that always 403s. The server was refusing correctly
    // throughout — what was missing was the screen admitting it before the round trip.
    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["outlet:read", "channel:read", "config:read"],
    });

    render(<OutletForm />);

    expect(await screen.findByText(/do not have permission to change outlets/i)).toBeTruthy();
    expect(screen.queryByRole("button", { name: /^Save$/ })).toBeNull();

    // Hiding Save is not enough on its own. A form that accepts an hour of edits and then offers
    // nowhere to put them is a worse lie than one that refuses on save — so every control goes
    // down with it, including the contacts rows and the tenant's own custom fields.
    // `matches(":disabled")`, not `.disabled`. The property reflects the element's own attribute,
    // and these controls do not have one — they are disabled by the `fieldset` around them, which
    // only the pseudo-class sees. Asserting `.disabled` here passed as `false` on a form that was
    // genuinely dead.
    await waitFor(() =>
      expect(screen.getByLabelText(/outlet name/i).matches(":disabled")).toBe(true),
    );
    expect(screen.getByLabelText(/channel/i).matches(":disabled")).toBe(true);
    expect(screen.getByRole("button", { name: /add contact/i }).matches(":disabled")).toBe(true);
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
    await userEvent.click(await screen.findByRole("button", { name: "Save" }));

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

    await userEvent.click(await screen.findByRole("button", { name: "Save" }));
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
    await userEvent.click(await screen.findByRole("button", { name: "Save" }));

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
    await userEvent.click(await screen.findByRole("button", { name: "Save" }));

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
    await userEvent.click(await screen.findByRole("button", { name: "Save" }));

    expect((await screen.findByRole("alert")).textContent).toContain("A contact needs a name.");
  });

  it("keeps a time zone the browser does not list", async () => {
    // Found in the Phase 1 demo. `UTC` is a zone the API accepts and stores, and one
    // `Intl.supportedValuesOf` does not enumerate — so the required select rendered empty, and
    // saving would have forced a different zone onto the outlet, moving its business day and the
    // validity window of every promotion on it.
    render(<OutletForm outlet={{ ...OUTLET, timeZoneId: "UTC" }} />);
    await waitFor(() => expect(fetchChannels).toHaveBeenCalled());

    expect((screen.getByLabelText(/time zone/i) as HTMLSelectElement).value).toBe("UTC");

    await userEvent.click(await screen.findByRole("button", { name: "Save" }));
    await waitFor(() => expect(updateOutlet).toHaveBeenCalled());

    expect(sent(updateOutlet).timeZoneId).toBe("UTC");
  });

  it("goes to the outlet it just saved", async () => {

    render(<OutletForm outlet={OUTLET} />);

    await userEvent.click(await screen.findByRole("button", { name: "Save" }));

    await waitFor(() => expect(push).toHaveBeenCalledWith("/outlets/019f-1"));
  });
});

