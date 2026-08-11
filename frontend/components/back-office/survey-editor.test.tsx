// @vitest-environment jsdom

import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { SurveyEditor } from "@/components/back-office/survey-editor";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { SurveyForm } from "@/lib/api/surveys";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const replace = vi.hoisted(() => vi.fn());
const fetchSurveys = vi.hoisted(() => vi.fn());
const createSurvey = vi.hoisted(() => vi.fn());
const setSurvey = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));
vi.mock("@/i18n/navigation", () => ({ useRouter: () => ({ replace }) }));

vi.mock("@/lib/api/surveys", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/surveys")>()),
  fetchSurveys: (...args: unknown[]) => fetchSurveys(...args),
  createSurvey: (...args: unknown[]) => createSurvey(...args),
  setSurvey: (...args: unknown[]) => setSurvey(...args),
}));

const FORM: SurveyForm = {
  id: "form-1",
  name: "Compliance",
  questions: [
    {
      order: 1,
      key: "chiller_clean",
      text: "Is the chiller clean?",
      type: "Boolean",
      mandatory: true,
      options: [],
    },
    {
      order: 2,
      key: "facings",
      text: "How many facings?",
      type: "Number",
      mandatory: false,
      options: [],
    },
  ],
};

function signedIn(permissions: readonly string[]): void {
  vi.mocked(fetchIdentity).mockResolvedValue({
    subject: "subject-a",
    tenant: "fieldkit-dev",
    permissions: [...permissions],
  });

  auth.current = {
    status: "authenticated",
    user: { access_token: "token", profile: { sub: "subject-a" } },
    workspace: "fieldkit-dev",
    signIn: vi.fn(),
    signOut: vi.fn(),
    completeSignIn: vi.fn(),
  } as unknown as AuthContextValue;
}

/** Waits for the permission answer — every write control only exists once it has arrived. */
async function ready(): Promise<void> {
  await screen.findByRole("button", { name: "Add question" });
}

describe("<SurveyEditor>", () => {
  beforeEach(() => {
    replace.mockReset();
    fetchSurveys.mockReset().mockResolvedValue([FORM]);
    createSurvey.mockReset().mockResolvedValue({ ...FORM, id: "form-9" });
    setSurvey.mockReset().mockResolvedValue(FORM);

    signedIn(["config:read", "config:write"]);
  });

  it("shows the questions in the order the rep will meet them", async () => {
    render(<SurveyEditor formId="form-1" />);
    await ready();

    const questions = screen.getAllByRole("listitem");

    expect(within(questions[0]).getByText("Question 1")).toBeTruthy();
    expect((within(questions[0]).getByLabelText("Question") as HTMLInputElement).value).toBe(
      "Is the chiller clean?",
    );
    expect((within(questions[1]).getByLabelText("Question") as HTMLInputElement).value).toBe(
      "How many facings?",
    );
  });

  it("will not let a saved question's key be edited", async () => {
    /*
     * The screen's one real rule, and it is a client-side policy: a `PUT` replaces the questions
     * wholesale and the API would accept a renamed key without complaint. An answer is filed under
     * the key (`AUD-09`), and Configuration cannot see whether a rep has answered — so the only safe
     * assumption about a saved question is that somebody has.
     */
    render(<SurveyEditor formId="form-1" />);
    await ready();

    const first = screen.getAllByRole("listitem")[0];
    const key = within(first).getByLabelText("Key") as HTMLInputElement;

    expect(key.value).toBe("chiller_clean");
    expect(key.disabled).toBe(true);

    // Disabled rather than hidden: it is what a report groups by, so it is worth reading.
    expect(within(first).getByText(/Fixed — answers are already filed under it/)).toBeTruthy();
  });

  it("writes a new question's key from its text, and stops when the text is edited later", async () => {
    const user = userEvent.setup();

    render(<SurveyEditor formId="form-1" />);
    await ready();

    await user.click(screen.getByRole("button", { name: "Add question" }));

    const added = screen.getAllByRole("listitem")[2];
    await user.type(within(added).getByLabelText("Question"), "Shelf temperature");

    const key = within(added).getByLabelText("Key") as HTMLInputElement;

    expect(key.value).toBe("shelf_temperature");
    expect(key.disabled).toBe(false);
  });

  it("names a key two questions derived alike, and refuses to save until it is fixed", async () => {
    /*
     * The collision the screen causes rather than the admin: two questions worded alike derive the
     * same key. The server refuses it with `config.survey.duplicateKey`, which names neither
     * question — an admin holding twelve of them cannot act on that.
     */
    const user = userEvent.setup();

    render(<SurveyEditor formId="form-1" />);
    await ready();

    await user.click(screen.getByRole("button", { name: "Add question" }));
    await user.type(screen.getAllByRole("listitem")[2].querySelector("input")!, "Facings");

    // **Both** are marked, not the second. Whichever one is renamed fixes it, and marking only the
    // newcomer would claim the older question is the right one — which is a guess the screen has no
    // basis for.
    expect(screen.getAllByText(/Two questions share this key/)).toHaveLength(2);
    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(true);

    expect(setSurvey).not.toHaveBeenCalled();
  });

  it("reorders with buttons and sends the order it shows", async () => {
    /*
     * The wireframe draws a drag handle. A drag-only reorder cannot be operated from a keyboard and
     * is invisible to a screen reader, and order is the whole meaning of this list — so the move is
     * a pair of buttons, and it is announced.
     */
    const user = userEvent.setup();

    render(<SurveyEditor formId="form-1" />);
    await ready();

    await user.click(screen.getByRole("button", { name: "Move question 2 up" }));

    const questions = screen.getAllByRole("listitem");
    expect((within(questions[0]).getByLabelText("Question") as HTMLInputElement).value).toBe(
      "How many facings?",
    );

    expect(screen.getByRole("status").textContent).toBe("How many facings? moved to position 1.");

    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(setSurvey).toHaveBeenCalled());

    expect(setSurvey.mock.calls[0][3].map((question: { key: string }) => question.key)).toEqual([
      "facings",
      "chiller_clean",
    ]);
  });

  it("offers options for a choice question only, and refuses an empty list", async () => {
    const user = userEvent.setup();

    render(<SurveyEditor formId="form-1" />);
    await ready();

    const facings = screen.getAllByRole("listitem")[1];

    // A number question has no list to offer.
    expect(within(facings).queryByLabelText("Options")).toBeNull();

    await user.selectOptions(within(facings).getByLabelText("Type"), "SingleChoice");

    expect(within(facings).getByLabelText("Options")).toBeTruthy();
    expect(within(facings).getByText(/needs something to choose from/)).toBeTruthy();
    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(true);

    await user.type(within(facings).getByLabelText("Options"), "One\nTwo");

    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("sends no options for a type that cannot carry them", async () => {
    // The server drops them itself. Sending them anyway would have the wire say something the
    // stored form does not — and they would become authoritative again on a switch back.
    const user = userEvent.setup();

    render(<SurveyEditor formId="form-1" />);
    await ready();

    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(setSurvey).toHaveBeenCalled());

    expect(setSurvey.mock.calls[0][3][0].options).toBeNull();
  });

  it("says what changing a saved question's type costs, without blocking it", async () => {
    /*
     * Allowed — the server takes it. Worth saying because the answers already filed under this key
     * were of the old shape and nothing rewrites them, so a report reading the key afterwards reads
     * two kinds of answer.
     */
    const user = userEvent.setup();

    render(<SurveyEditor formId="form-1" />);
    await ready();

    const chiller = screen.getAllByRole("listitem")[0];
    await user.selectOptions(within(chiller).getByLabelText("Type"), "Text");

    expect(
      within(chiller).getByText("Answers already given under this key were Yes or no."),
    ).toBeTruthy();

    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("creates a form and stops being a new one", async () => {
    // Left on `/new`, the next Save would create a second form.
    const user = userEvent.setup();

    render(<SurveyEditor formId={null} />);
    await ready();

    await user.type(screen.getByLabelText("Name"), "Brand survey");
    await user.click(screen.getByRole("button", { name: "Add question" }));
    await user.type(screen.getByLabelText("Question"), "Is the display up?");

    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(createSurvey).toHaveBeenCalled());

    expect(createSurvey).toHaveBeenCalledWith("token", "Brand survey", [
      {
        key: "is_the_display_up",
        text: "Is the display up?",
        type: "Text",
        mandatory: false,
        options: null,
      },
    ]);

    await waitFor(() => expect(replace).toHaveBeenCalledWith("/configuration/surveys/form-9"));
  });

  it("will not save a form that asks nothing", async () => {
    // The refusal an admin meets by doing nothing rather than by doing something wrong, so it is
    // said before Save rather than by it.
    fetchSurveys.mockResolvedValue([]);

    render(<SurveyEditor formId={null} />);
    await ready();

    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText("Add a question first.")).toBeTruthy();
  });

  it("removes a question", async () => {
    const user = userEvent.setup();

    render(<SurveyEditor formId="form-1" />);
    await ready();

    await user.click(screen.getByRole("button", { name: "Remove question 1" }));

    expect(screen.getAllByRole("listitem")).toHaveLength(1);
    expect((screen.getByLabelText("Question") as HTMLInputElement).value).toBe(
      "How many facings?",
    );
  });

  it("shows the server's refusal in the reader's language", async () => {
    const user = userEvent.setup();

    /*
     * The refusal carries the name as an **argument**, and the catalogue entry spends it. A
     * translated sentence cannot dig a value out of the server's English, and an entry naming a
     * placeholder the server does not send throws inside `next-intl` at render — so the argument and
     * the entry have to agree. The server side of that agreement is asserted in `SurveyTests`.
     */
    setSurvey.mockRejectedValue(
      new ApiError(409, [
        {
          field: "name",
          code: "config.survey.nameTaken",
          message: "'Compliance' is already the name of a survey.",
          args: { name: "Compliance" },
        },
      ]),
    );

    render(<SurveyEditor formId="form-1" />);
    await ready();

    await user.click(screen.getByRole("button", { name: "Save" }));

    expect((await screen.findByRole("alert")).textContent).toContain(
      "A survey named “Compliance” already exists.",
    );
  });

  it("shows a reader the questions and none of the controls", async () => {
    // `config:read` without `config:write`. Not disabled controls — a reader is not a writer who has
    // been stopped, and a dead button explains nothing about why.
    signedIn(["config:read"]);

    render(<SurveyEditor formId="form-1" />);

    expect(await screen.findByDisplayValue("Is the chiller clean?")).toBeTruthy();

    expect(screen.queryByRole("button", { name: "Add question" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Save" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Move question 2 up" })).toBeNull();
    expect((screen.getByLabelText("Name") as HTMLInputElement).disabled).toBe(true);
  });

  it("says so when the address names no survey", async () => {
    render(<SurveyEditor formId="missing" />);

    expect((await screen.findByRole("alert")).textContent).toContain(
      "There is no survey with that address.",
    );
  });
});
