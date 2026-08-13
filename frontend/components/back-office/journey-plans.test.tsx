// @vitest-environment jsdom

import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { JourneyPlans } from "@/components/back-office/journey-plans";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { GeneratedPlan, JourneyPlan, JourneyPlanDetail } from "@/lib/api/journeys";
import type { User } from "@/lib/api/users";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchPlans = vi.hoisted(() => vi.fn());
const fetchPlan = vi.hoisted(() => vi.fn());
const generatePlan = vi.hoisted(() => vi.fn());
const publishPlan = vi.hoisted(() => vi.fn());
const fetchOutlets = vi.hoisted(() => vi.fn());
const fetchUsers = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

vi.mock("@/lib/api/journeys", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/journeys")>()),
  fetchPlans: (...args: unknown[]) => fetchPlans(...args),
  fetchPlan: (...args: unknown[]) => fetchPlan(...args),
  generatePlan: (...args: unknown[]) => generatePlan(...args),
  publishPlan: (...args: unknown[]) => publishPlan(...args),
}));

vi.mock("@/lib/api/outlets", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/outlets")>()),
  fetchOutlets: (...args: unknown[]) => fetchOutlets(...args),
}));

vi.mock("@/lib/api/users", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/users")>()),
  fetchUsers: (...args: unknown[]) => fetchUsers(...args),
}));

const DRAFT: JourneyPlan = {
  id: "plan-1",
  userId: "subject-maria",
  displayName: "Maria Ionescu",
  from: "2028-03-06",
  to: "2028-03-12",
  status: "Draft",
  visitCount: 3,
  shortfallCount: 1,
  generatedAtUtc: "2028-03-01T09:00:00Z",
  publishedAtUtc: null,
};

const DETAIL: JourneyPlanDetail = {
  plan: DRAFT,
  visits: [
    {
      id: "v-1",
      date: "2028-03-06",
      outletId: "outlet-1",
      status: "Planned",
      source: "Generated",
      notVisitedReason: null,
      rescheduledFrom: null,
    },
    {
      id: "v-2",
      date: "2028-03-06",
      outletId: "outlet-2",
      status: "NotVisited",
      source: "Generated",
      notVisitedReason: "Shutters down",
      rescheduledFrom: null,
    },
    {
      id: "v-3",
      date: "2028-03-08",
      outletId: "outlet-1",
      status: "Planned",
      source: "Generated",
      notVisitedReason: null,
      rescheduledFrom: null,
    },
  ],
  shortfalls: [{ outletId: "outlet-2", required: 4, planned: 1 }],
};

const OUTLETS = {
  items: [
    { id: "outlet-1", code: "RO-0001", name: "Corner Shop" },
    { id: "outlet-2", code: "RO-0002", name: "Kiosk 1 Mai" },
    { id: "outlet-3", code: "RO-0003", name: "Shut Shop" },
  ],
  total: 3,
  page: 1,
  pageSize: 200,
};

const USERS: User[] = [
  {
    id: "u-1",
    subjectId: "subject-maria",
    email: "maria@fieldkit.local",
    displayName: "Maria Ionescu",
    locale: "ro-RO",
    timeZone: "Europe/Bucharest",
    isActive: true,
    roleIds: [],
  },
];

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

describe("<JourneyPlans>", () => {
  beforeEach(() => {
    fetchPlans.mockReset().mockResolvedValue([DRAFT]);
    fetchPlan.mockReset().mockResolvedValue(DETAIL);
    generatePlan.mockReset();
    publishPlan.mockReset().mockResolvedValue({ ...DRAFT, status: "Published" });
    fetchOutlets.mockReset().mockResolvedValue(OUTLETS);
    fetchUsers.mockReset().mockResolvedValue(USERS);

    signedIn(["journey:read", "journey:write", "user:read", "outlet:read"]);
  });

  it("names every shop on the plan in one request", async () => {
    // The reason `ids` exists on the outlet list. One GET per shop is what the picker's own note
    // says to replace when a screen like this arrives — and a plan is hundreds of visits.
    render(<JourneyPlans />);

    expect((await screen.findAllByText("Corner Shop")).length).toBeGreaterThan(0);

    await waitFor(() => expect(fetchOutlets).toHaveBeenCalledTimes(1));

    const [, query] = fetchOutlets.mock.calls[0] as [string, { ids: string[] }];

    // Distinct, and every shop the plan mentions — including the one that only appears in a
    // shortfall, which would otherwise render as "Unknown shop" beside a real number.
    expect(query.ids).toEqual(["outlet-1", "outlet-2"]);
  });

  it("groups the calls by day, in the order they happen", async () => {
    render(<JourneyPlans />);

    const [chip] = await screen.findAllByText("Corner Shop");
    const columns = chip.closest("div.flex.gap-3")!.querySelectorAll("section");

    // Two days, not seven: a window is whatever was asked for, and a fixed Mon–Fri grid would invent
    // empty columns for a three-week plan or crop a Saturday somebody works.
    expect(columns).toHaveLength(2);
  });

  it("keeps a skipped call on the plan, with the reason the rep gave", async () => {
    // BR-JRN-2. Letting it disappear would make coverage look complete.
    render(<JourneyPlans />);

    expect(await screen.findByText("Shutters down")).toBeTruthy();

    // Awaited, because the names arrive after the plan does — the grid renders from the plan and
    // fills in shop names when the one outlet request lands.
    // Twice on purpose: once as the call it still is, once in the shortfall it caused.
    await waitFor(() => expect(screen.getAllByText("Kiosk 1 Mai")).toHaveLength(2));
  });

  it("says what the plan fell short on, and by how much", async () => {
    render(<JourneyPlans />);

    expect(await screen.findByText("Called on less often than due")).toBeTruthy();
    expect(screen.getByText("1 of 4 calls")).toBeTruthy();
  });

  it("lets a supervisor pick between two reps with the same name", async () => {
    // The picker showed `displayName` alone, so two people called Maria Ionescu were two identical
    // rows — and the choice decides whose week gets generated. Selection is by the *visible* label
    // rather than by subject id, because a value the supervisor cannot read is not a choice.
    const second: User = {
      ...USERS[0],
      id: "u-2",
      subjectId: "subject-maria-2",
      email: "m.ionescu@fieldkit.local",
    };

    fetchUsers.mockResolvedValue([USERS[0], second]);
    generatePlan.mockResolvedValue({ ...DETAIL, excluded: [] });

    render(<JourneyPlans />);

    const picker = await screen.findByLabelText("Rep");
    const chosen = await screen.findByRole("option", { name: /m\.ionescu@fieldkit\.local/ });

    // Both are offered, and they read differently — the assertion the old markup failed.
    expect(screen.getByRole("option", { name: /maria@fieldkit\.local/ })).toBeTruthy();
    expect(chosen.textContent).not.toBe(
      screen.getByRole("option", { name: /maria@fieldkit\.local/ }).textContent,
    );

    await userEvent.selectOptions(picker, chosen);
    await userEvent.type(screen.getByLabelText("From"), "2028-03-06");
    await userEvent.type(screen.getByLabelText("To"), "2028-03-12");
    await userEvent.click(screen.getByRole("button", { name: "Generate" }));

    // The half that makes the label worth having: the second Maria is the one planned for.
    await waitFor(() =>
      expect(generatePlan).toHaveBeenCalledWith(
        expect.anything(),
        "subject-maria-2",
        "2028-03-06",
        "2028-03-12",
      ),
    );
  });

  it("shows what was excluded only for the run that reported it", async () => {
    // An exclusion is a fact about the *inputs* — a shut shop, or one with no frequency — so it is
    // returned by generation and stored nowhere. Re-reading a plan cannot show it, and showing the
    // last run's exclusions against a different plan would be a lie about that plan.
    const generated: GeneratedPlan = {
      ...DETAIL,
      excluded: [{ outletId: "outlet-3", reason: "NoFrequency" }],
    };

    generatePlan.mockResolvedValue(generated);

    render(<JourneyPlans />);
    await screen.findAllByText("Corner Shop");

    expect(screen.queryByText("Not planned at all")).toBeNull();

    await userEvent.selectOptions(screen.getByLabelText("Rep"), "subject-maria");
    await userEvent.type(screen.getByLabelText("From"), "2028-03-06");
    await userEvent.type(screen.getByLabelText("To"), "2028-03-12");
    await userEvent.click(screen.getByRole("button", { name: "Generate" }));

    expect(await screen.findByText("Not planned at all")).toBeTruthy();
    expect(screen.getByText("Shut Shop")).toBeTruthy();
    expect(screen.getByText("No call frequency")).toBeTruthy();
  });

  it("does not show one run's exclusions against another plan", async () => {
    // The half of that rule a single-plan test cannot see. Exclusions belong to the run that
    // reported them; carrying them onto the plan a supervisor clicks next would attribute a shut
    // shop to a plan that never considered it.
    const earlier: JourneyPlan = { ...DRAFT, id: "plan-0", from: "2028-02-28", to: "2028-03-05" };

    fetchPlans.mockResolvedValue([DRAFT, earlier]);
    generatePlan.mockResolvedValue({
      ...DETAIL,
      excluded: [{ outletId: "outlet-3", reason: "Closed" }],
    } satisfies GeneratedPlan);

    render(<JourneyPlans />);
    await screen.findAllByText("Corner Shop");

    await userEvent.selectOptions(screen.getByLabelText("Rep"), "subject-maria");
    await userEvent.type(screen.getByLabelText("From"), "2028-03-06");
    await userEvent.type(screen.getByLabelText("To"), "2028-03-12");
    await userEvent.click(screen.getByRole("button", { name: "Generate" }));

    expect(await screen.findByText("Not planned at all")).toBeTruthy();

    fetchPlan.mockResolvedValue({ ...DETAIL, plan: earlier });

    // The earlier window, which is the second row.
    await userEvent.click(screen.getAllByRole("button", { name: /2028/ })[1]);

    await waitFor(() => expect(screen.queryByText("Not planned at all")).toBeNull());
  });

  it("refuses a backwards window without asking the server", async () => {
    render(<JourneyPlans />);
    await screen.findAllByText("Corner Shop");

    await userEvent.selectOptions(screen.getByLabelText("Rep"), "subject-maria");
    await userEvent.type(screen.getByLabelText("From"), "2028-03-12");
    await userEvent.type(screen.getByLabelText("To"), "2028-03-06");

    expect(screen.getByLabelText("To").getAttribute("aria-invalid")).toBe("true");
    expect((screen.getByRole("button", { name: "Generate" }) as HTMLButtonElement).disabled).toBe(
      true,
    );
    expect(generatePlan).not.toHaveBeenCalled();
  });

  it("offers Publish on a draft and not on a published plan", async () => {
    // The separation the slice exists for: until it happens, a plan is an experiment.
    render(<JourneyPlans />);

    expect(await screen.findByRole("button", { name: "Publish" })).toBeTruthy();

    fetchPlan.mockResolvedValue({ ...DETAIL, plan: { ...DRAFT, status: "Published" } });
    fetchPlans.mockResolvedValue([{ ...DRAFT, status: "Published" }]);

    await userEvent.click(screen.getByRole("button", { name: "Publish" }));

    await waitFor(() => expect(publishPlan).toHaveBeenCalledWith("token", "plan-1"));
    await waitFor(() => expect(screen.queryByRole("button", { name: "Publish" })).toBeNull());

    expect(screen.getByText(/This is the round the rep is working/)).toBeTruthy();
  });

  it("says a second publish is a misunderstanding, in the reader's language", async () => {
    publishPlan.mockRejectedValue(
      new ApiError(409, [
        {
          field: null,
          message: "This plan is already published. Generate a new one instead.",
          code: "journey.plan.alreadyPublished",
        },
      ]),
    );

    render(<JourneyPlans />);

    await userEvent.click(await screen.findByRole("button", { name: "Publish" }));

    const alert = await screen.findByRole("alert");

    expect(alert.textContent).toMatch(/already published/);
  });

  it("shows a plan with no calls as an answer rather than an error", async () => {
    // Nobody has a frequency, or the rep works no days in the window. The shortfalls below say
    // which — and an empty grid is the honest rendering of both.
    fetchPlan.mockResolvedValue({ ...DETAIL, visits: [], shortfalls: [] });

    render(<JourneyPlans />);

    expect(await screen.findByText(/No calls\./)).toBeTruthy();
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("shows a reader the plans and no way to change them", async () => {
    signedIn(["journey:read", "outlet:read"]);

    render(<JourneyPlans />);

    expect((await screen.findAllByText("Corner Shop")).length).toBeGreaterThan(0);

    await waitFor(() => expect(screen.queryByLabelText("Rep")).toBeNull());

    expect(screen.queryByRole("button", { name: "Generate" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Publish" })).toBeNull();
  });

  it("asks for no names when a plan mentions no shops", async () => {
    // `ids=` means "none of them", so asking with an empty list would be a request for nothing —
    // and skipping the parameter entirely would ask for the whole outlet base.
    fetchPlan.mockResolvedValue({ ...DETAIL, visits: [], shortfalls: [] });

    render(<JourneyPlans />);
    await screen.findByText(/No calls\./);

    expect(fetchOutlets).not.toHaveBeenCalled();
  });

  it("keeps every run rather than editing the last one", async () => {
    const published: JourneyPlan = {
      ...DRAFT,
      id: "plan-0",
      status: "Published",
      from: "2028-02-28",
      to: "2028-03-05",
    };

    fetchPlans.mockResolvedValue([DRAFT, published]);

    render(<JourneyPlans />);

    const list = (await screen.findByText("Plans")).parentElement!;

    expect(within(list).getAllByRole("button")).toHaveLength(2);
  });
});
