// @vitest-environment jsdom

import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { SurveyBrowser } from "@/components/back-office/survey-browser";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { SurveyForm } from "@/lib/api/surveys";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchSurveys = vi.hoisted(() => vi.fn());
const deleteSurvey = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

vi.mock("@/lib/api/surveys", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/surveys")>()),
  fetchSurveys: (...args: unknown[]) => fetchSurveys(...args),
  deleteSurvey: (...args: unknown[]) => deleteSurvey(...args),
}));

function form(id: string, name: string, mandatory: number, optional: number): SurveyForm {
  const questions = [
    ...Array.from({ length: mandatory }, (_, index) => ({ index, mandatory: true })),
    ...Array.from({ length: optional }, (_, index) => ({ index: mandatory + index, mandatory: false })),
  ];

  return {
    id,
    name,
    questions: questions.map(({ index, mandatory: required }) => ({
      order: index + 1,
      key: `q_${index}`,
      text: `Question ${index}?`,
      type: "Text" as const,
      mandatory: required,
      options: [],
    })),
  };
}

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

describe("<SurveyBrowser>", () => {
  beforeEach(() => {
    fetchSurveys
      .mockReset()
      .mockResolvedValue([form("form-1", "Chiller compliance", 2, 1), form("form-2", "Brand lift", 0, 3)]);

    deleteSurvey.mockReset().mockResolvedValue(undefined);

    signedIn(["config:read", "config:write"]);
  });

  it("says what each survey asks without opening it", async () => {
    // The second number is the one that decides whether an audit step can be finished at all
    // (`BR-AUD-7`), so it is worth seeing from the list.
    render(<SurveyBrowser />);

    const first = (await screen.findAllByRole("listitem"))[0];

    expect(within(first).getByText("Chiller compliance")).toBeTruthy();
    expect(within(first).getByText(/3 questions/)).toBeTruthy();
    expect(within(first).getByText(/2 must be answered/)).toBeTruthy();
  });

  it("says nothing about mandatory questions when there are none", async () => {
    // "0 must be answered" is a fact nobody needs and a number that reads as a warning.
    render(<SurveyBrowser />);

    const second = (await screen.findAllByRole("listitem"))[1];

    expect(within(second).getByText(/3 questions/)).toBeTruthy();
    expect(within(second).queryByText(/must be answered/)).toBeNull();
  });

  it("keeps the order the server sent", async () => {
    /*
     * The API returns them by name. Re-sorting here would be a second answer to "which is first",
     * and the first screen to disagree with the API is the one that has to be explained.
     */
    render(<SurveyBrowser />);

    const rows = await screen.findAllByRole("listitem");

    expect(rows.map((row) => row.querySelector("span")?.textContent)).toEqual([
      "Chiller compliance",
      "Brand lift",
    ]);
  });

  it("opens a survey with a link, not a button", async () => {
    // It navigates. A link can be opened in a new tab and is listed as a link by a screen reader;
    // a button announced for a navigation is a control that lies about what it does.
    render(<SurveyBrowser />);

    const open = await screen.findByRole("link", { name: "Open Chiller compliance" });

    expect(open.getAttribute("href")).toBe("/en/configuration/surveys/form-1");
  });

  it("says what deleting does not do before it deletes", async () => {
    /*
     * The opposite shape from the custom-field catalogue's warning, and the more surprising fact:
     * the answers stay, and stay readable, because each carries its question's wording. An
     * administrator hesitating here is asking "do I lose the history?" — so the sentence answers
     * that rather than asking whether they are sure.
     */
    const user = userEvent.setup();

    render(<SurveyBrowser />);

    await user.click(await screen.findByRole("button", { name: "Delete Chiller compliance" }));

    expect(screen.getByText(/Answers already given stay where they are and stay readable/)).toBeTruthy();
    expect(deleteSurvey).not.toHaveBeenCalled();

    await user.click(screen.getByRole("button", { name: "Delete it" }));

    await waitFor(() => expect(deleteSurvey).toHaveBeenCalledWith("token", "form-1"));
  });

  it("lets the confirmation be backed out of", async () => {
    const user = userEvent.setup();

    render(<SurveyBrowser />);

    await user.click(await screen.findByRole("button", { name: "Delete Brand lift" }));
    await user.click(screen.getByRole("button", { name: "Cancel" }));

    expect(screen.queryByText(/Answers already given/)).toBeNull();
    expect(deleteSurvey).not.toHaveBeenCalled();
  });

  it("confirms one survey at a time", async () => {
    // The confirmation is keyed by id rather than a boolean, so pressing Delete on a second row
    // moves the question instead of asking it twice with one answer.
    const user = userEvent.setup();

    render(<SurveyBrowser />);

    await user.click(await screen.findByRole("button", { name: "Delete Chiller compliance" }));
    await user.click(screen.getByRole("button", { name: "Delete Brand lift" }));

    expect(screen.getAllByRole("alert")).toHaveLength(1);

    await user.click(screen.getByRole("button", { name: "Delete it" }));

    await waitFor(() => expect(deleteSurvey).toHaveBeenCalledWith("token", "form-2"));
  });

  it("shows a reader the surveys and a way to read one", async () => {
    // `config:read` without `config:write`. The editor renders read-only for them, so the route in
    // is still offered — hiding it would make the list a dead end rather than a read-only one.
    signedIn(["config:read"]);

    render(<SurveyBrowser />);

    expect(await screen.findByRole("link", { name: "Open Chiller compliance" })).toBeTruthy();

    // Both rows, and the word is "View" rather than "Edit": the link goes to the same place either
    // way, so the label is the only thing that can say what will happen when they arrive.
    expect(screen.getAllByText("View")).toHaveLength(2);
    expect(screen.queryByText("Edit")).toBeNull();

    expect(screen.queryByRole("link", { name: "New survey" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Delete Chiller compliance" })).toBeNull();
  });

  it("says what an empty tenant means rather than showing nothing", async () => {
    fetchSurveys.mockResolvedValue([]);

    render(<SurveyBrowser />);

    expect(await screen.findByText(/an audit has no questions to carry/)).toBeTruthy();
  });

  it("says so when the reader may not see surveys at all", async () => {
    fetchSurveys.mockRejectedValue(new ApiError(403, []));

    render(<SurveyBrowser />);

    expect((await screen.findByRole("alert")).textContent).toContain(
      "You do not have permission to see surveys.",
    );
  });
});
