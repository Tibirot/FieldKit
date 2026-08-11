// @vitest-environment jsdom

import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { FieldDefinitionBrowser } from "@/components/back-office/field-definition-browser";
import { ApiError } from "@/lib/api/client";
import type { FieldDefinition } from "@/lib/api/field-definitions";
import { fetchIdentity } from "@/lib/api/identity";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchFieldDefinitions = vi.hoisted(() => vi.fn());
const createFieldDefinition = vi.hoisted(() => vi.fn());
const updateFieldDefinition = vi.hoisted(() => vi.fn());
const deleteFieldDefinition = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

vi.mock("@/lib/api/field-definitions", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/field-definitions")>()),
  fetchFieldDefinitions: (...args: unknown[]) => fetchFieldDefinitions(...args),
  createFieldDefinition: (...args: unknown[]) => createFieldDefinition(...args),
  updateFieldDefinition: (...args: unknown[]) => updateFieldDefinition(...args),
  deleteFieldDefinition: (...args: unknown[]) => deleteFieldDefinition(...args),
}));

const CHILLERS: FieldDefinition = {
  id: "fd-chillers",
  entity: "Outlet",
  key: "chiller_count",
  label: "Chiller count",
  type: "Number",
  required: false,
  options: [],
  maxLength: null,
  minimum: 0,
  maximum: 20,
};

const OWNERSHIP: FieldDefinition = {
  id: "fd-ownership",
  entity: "Outlet",
  key: "ownership",
  label: "Ownership",
  type: "Choice",
  required: true,
  options: ["Franchise", "Owned"],
  maxLength: null,
  minimum: null,
  maximum: null,
};

describe("<FieldDefinitionBrowser>", () => {
  beforeEach(() => {
    fetchFieldDefinitions.mockReset().mockResolvedValue([CHILLERS, OWNERSHIP]);
    createFieldDefinition.mockReset().mockResolvedValue(CHILLERS);
    updateFieldDefinition.mockReset().mockResolvedValue(CHILLERS);
    deleteFieldDefinition.mockReset().mockResolvedValue(undefined);

    auth.current = {
      status: "authenticated",
      user: { access_token: "token", profile: { sub: "subject-a" } },
      workspace: "fieldkit-dev",
      signIn: vi.fn(),
      signOut: vi.fn(),
      completeSignIn: vi.fn(),
    } as unknown as AuthContextValue;
  });

  it("shows each field's key and the rule it carries", async () => {
    // The key, because it is what the import's column headers must match and what the values are
    // stored under — a screen showing only labels cannot answer "why was my column refused".
    render(<FieldDefinitionBrowser entity="Outlet" />);

    const items = await screen.findAllByRole("listitem");

    expect(items[0].textContent).toContain("chiller_count");
    expect(items[0].textContent).toContain("0 – 20");
    expect(items[1].textContent).toContain("Franchise, Owned");
    expect(items[1].textContent).toContain("Required");
  });

  it("tells an empty workspace that outlets still work without one", async () => {
    // Unlike channels, where empty is a dead end. A tenant with no custom fields has a working
    // outlet base, and saying so is the difference between "nothing here yet" and "something broke".
    fetchFieldDefinitions.mockResolvedValue([]);

    render(<FieldDefinitionBrowser entity="Outlet" />);

    expect(await screen.findByText(/standard fields only/)).toBeTruthy();
  });

  it("fills the key in from the label, so an admin does not have to know the format", async () => {
    render(<FieldDefinitionBrowser entity="Outlet" />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "New field" }));
    await userEvent.type(screen.getByLabelText("Label"), "Shelf space");

    expect((screen.getByLabelText("Key") as HTMLInputElement).value).toBe("shelf_space");
  });

  it("stops deriving the key once someone has typed one", async () => {
    // Otherwise a deliberate key is silently overwritten by the next keystroke in the label, and
    // the field it names is fixed forever a moment later.
    render(<FieldDefinitionBrowser entity="Outlet" />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "New field" }));
    await userEvent.type(screen.getByLabelText("Key"), "sqm");
    await userEvent.type(screen.getByLabelText("Label"), "Shelf space");

    expect((screen.getByLabelText("Key") as HTMLInputElement).value).toBe("sqm");
  });

  it("creates one", async () => {
    render(<FieldDefinitionBrowser entity="Outlet" />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "New field" }));
    await userEvent.type(screen.getByLabelText("Label"), "Ownership");
    await userEvent.selectOptions(screen.getByLabelText("Type"), "Choice");
    await userEvent.type(screen.getByLabelText("Options"), "Franchise\nOwned");
    await userEvent.click(screen.getByLabelText("Required"));
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(createFieldDefinition).toHaveBeenCalled());

    expect(createFieldDefinition).toHaveBeenCalledWith("token", "Outlet", {
      key: "ownership",
      label: "Ownership",
      type: "Choice",
      required: true,
      options: ["Franchise", "Owned"],
      maxLength: null,
      minimum: null,
      maximum: null,
    });
  });

  it("sends only the constraints the chosen type can carry", async () => {
    // The bug this exists for: `chiller_count` is a number with bounds, and retyping it as text
    // leaves `minimum`/`maximum` in the payload. The server stores them — it only clears options —
    // so the bounds survive invisibly, render nowhere, validate nothing, and become authoritative
    // again the moment someone switches the field back to a number.
    render(<FieldDefinitionBrowser entity="Outlet" />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "Edit Chiller count" }));
    await userEvent.selectOptions(screen.getByLabelText("Type"), "Text");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(updateFieldDefinition).toHaveBeenCalled());

    expect(updateFieldDefinition).toHaveBeenCalledWith(
      "token",
      "fd-chillers",
      expect.objectContaining({ type: "Text", minimum: null, maximum: null }),
    );
  });

  it("will not let the key be edited on a definition that exists", async () => {
    // It is the JSONB property name already written into every outlet row; a rename orphans every
    // value stored under the old one. The API has no key on its update contract at all — this is
    // the same rule where an admin can see it.
    render(<FieldDefinitionBrowser entity="Outlet" />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "Edit Chiller count" }));

    const key = screen.getByLabelText("Key") as HTMLInputElement;

    expect(key.value).toBe("chiller_count");
    expect(key.disabled).toBe(true);
  });

  it("refuses a key the format cannot accept before asking the server", async () => {
    render(<FieldDefinitionBrowser entity="Outlet" />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "New field" }));
    await userEvent.type(screen.getByLabelText("Label"), "Shelf space");
    await userEvent.clear(screen.getByLabelText("Key"));
    await userEvent.type(screen.getByLabelText("Key"), "Shelf Space");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByText(/starting with a letter/)).toBeTruthy();
    expect(createFieldDefinition).not.toHaveBeenCalled();
  });

  it("puts a key the server refused under the key box", async () => {
    // Uniqueness per (entity, key) is the server's to know, and its problem field is already this
    // form's field name — so a conflict lands beside the control without a translation table.
    createFieldDefinition.mockRejectedValue(
      new ApiError(409, [{ field: "key", message: "'ownership' is already defined for Outlet." }]),
    );

    render(<FieldDefinitionBrowser entity="Outlet" />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "New field" }));
    await userEvent.type(screen.getByLabelText("Label"), "Ownership");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    const message = await screen.findByText(/already defined/);

    expect(screen.getByLabelText("Key").getAttribute("aria-describedby")).toBe(message.id);
  });

  it("names what a delete costs, and does not delete until that is acknowledged", async () => {
    // Configuration cannot clean another module's rows (ADR-0005), so the values stay in each
    // outlet's JSONB until its next save and then vanish. Nothing else would ever mention them.
    render(<FieldDefinitionBrowser entity="Outlet" />);
    await screen.findAllByRole("listitem");

    await userEvent.click(screen.getByRole("button", { name: "Delete Chiller count" }));

    const warning = await screen.findByRole("alert");

    expect(warning.textContent).toContain("chiller_count");
    expect(deleteFieldDefinition).not.toHaveBeenCalled();

    await userEvent.click(within(warning).getByRole("button", { name: "Delete anyway" }));

    await waitFor(() => expect(deleteFieldDefinition).toHaveBeenCalledWith("token", "fd-chillers"));
  });

  it("offers no way to change anything to a caller who may only read", async () => {
    // `config:write` decides what an outlet *is* — a required field added here is one every outlet
    // must now carry and one the import will refuse a file for. Maintaining outlets is not that.
    vi.mocked(fetchIdentity).mockResolvedValue({
      subject: "subject-a",
      tenant: "fieldkit-dev",
      permissions: ["config:read", "outlet:read", "outlet:write"],
    });

    render(<FieldDefinitionBrowser entity="Outlet" />);
    await screen.findAllByRole("listitem");

    expect(screen.queryByRole("button", { name: "New field" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Edit Chiller count" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Delete Chiller count" })).toBeNull();
    expect(screen.getByText("Chiller count")).toBeTruthy();
  });
});
