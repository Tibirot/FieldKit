// @vitest-environment jsdom

import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { ScoreWeights } from "@/components/back-office/score-weights";
import { ApiError } from "@/lib/api/client";
import { fetchIdentity } from "@/lib/api/identity";
import type { ScoreWeightSet } from "@/lib/api/score-weights";
import { render } from "@/test/render";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));
const fetchScoreWeights = vi.hoisted(() => vi.fn());
const draftScoreWeights = vi.hoisted(() => vi.fn());
const setScoreWeights = vi.hoisted(() => vi.fn());
const publishScoreWeights = vi.hoisted(() => vi.fn());

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

vi.mock("@/lib/api/score-weights", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/score-weights")>()),
  fetchScoreWeights: (...args: unknown[]) => fetchScoreWeights(...args),
  draftScoreWeights: (...args: unknown[]) => draftScoreWeights(...args),
  setScoreWeights: (...args: unknown[]) => setScoreWeights(...args),
  publishScoreWeights: (...args: unknown[]) => publishScoreWeights(...args),
}));

function weighting(
  version: number,
  isPublished: boolean,
  availability = 50,
  shelf = 30,
  price = 20,
): ScoreWeightSet {
  return {
    id: `set-${version}`,
    version,
    isPublished,
    publishedAtUtc: isPublished ? "2026-04-06T09:00:00+00:00" : null,
    weights: [
      { pillar: "Availability", percentage: availability },
      { pillar: "ShareOfShelf", percentage: shelf },
      { pillar: "PriceCompliance", percentage: price },
    ],
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

/** Waits for the permission answer — every write control only exists once it has arrived. */
async function ready(): Promise<void> {
  await screen.findByRole("button", { name: "New version" });
}

describe("<ScoreWeights>", () => {
  beforeEach(() => {
    fetchScoreWeights.mockReset().mockResolvedValue([weighting(2, false), weighting(1, true)]);
    draftScoreWeights.mockReset().mockResolvedValue(weighting(3, false));
    setScoreWeights.mockReset().mockResolvedValue(weighting(2, false));
    publishScoreWeights.mockReset().mockResolvedValue(weighting(2, true));

    signedIn(["config:read", "config:write"]);
  });

  it("shows every version, because sealed audits point at the old ones forever", async () => {
    // A list that hid published versions would hide the only way to read a historical score
    // (`BR-AUD-8`). Newest first, which is the version an administrator is looking at or about to
    // publish.
    render(<ScoreWeights />);

    expect(await screen.findByText("Version 2")).toBeTruthy();
    expect(screen.getByText("Version 1")).toBeTruthy();
    expect(screen.getByText("Draft")).toBeTruthy();
    expect(screen.getByText("Published")).toBeTruthy();
  });

  it("offers no edit control at all on a published version", async () => {
    /*
     * The screen's whole job. A disabled Edit would be a dead control that explains nothing — the
     * pattern this codebase keeps rejecting — so a published version simply does not have one, and
     * what it offers instead is the thing an administrator actually has to do.
     */
    render(<ScoreWeights />);
    await ready();

    // Exactly one Edit and one Publish, and they belong to the draft.
    expect(screen.getAllByRole("button", { name: "Edit" })).toHaveLength(1);
    expect(screen.getAllByRole("button", { name: "Publish" })).toHaveLength(1);

    // …and the published one offers the route forward instead.
    expect(
      screen.getByRole("button", { name: "Start a new version from this" }),
    ).toBeTruthy();
  });

  it("refuses to save a draft that does not add up to exactly 100", async () => {
    /*
     * `BR-AUD-4` has no tolerance, so a screen that let three numbers through and traded a refusal
     * for every near miss would be teaching an administrator the rule by failure. The total says how
     * far off it is while they type.
     */
    const user = userEvent.setup();

    render(<ScoreWeights />);
    await ready();

    await user.click(screen.getByRole("button", { name: "New version" }));

    const availability = screen.getByLabelText("Availability");
    await user.clear(availability);
    await user.type(availability, "60");

    expect(screen.getByText(/Total 110.00%/)).toBeTruthy();
    expect(screen.getByText(/10.00 over/)).toBeTruthy();
    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(true);

    expect(draftScoreWeights).not.toHaveBeenCalled();
  });

  it("adds up a set whose float sum misses 100, where a naive screen would refuse it", async () => {
    /*
     * `0.01 + 64.04 + 35.95` is `100.00000000000001` in float64. A screen summing that way would
     * show "0.00 over" and disable Save on a set the server stores happily — a refusal invented by
     * the client.
     *
     * `sumInHundredths` works in integer hundredths, so every intermediate is exact.
     * `lib/api/score-weights.test.ts` covers the arithmetic on its own; this covers the control it
     * gates.
     */
    const user = userEvent.setup();

    render(<ScoreWeights />);
    await ready();

    await user.click(screen.getByRole("button", { name: "New version" }));

    for (const [label, value] of [
      ["Availability", "0.01"],
      ["Share of shelf", "64.04"],
      ["Price compliance", "35.95"],
    ] as const) {
      const box = screen.getByLabelText(label);
      await user.clear(box);
      await user.type(box, value);
    }

    expect(screen.getByText(/Total 100.00%/)).toBeTruthy();
    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("drafts a new version without naming one", async () => {
    // The server assigns `Max + 1`. A client that could name its own could name one a sealed audit
    // already points at.
    const user = userEvent.setup();

    render(<ScoreWeights />);
    await ready();

    await user.click(screen.getByRole("button", { name: "New version" }));
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(draftScoreWeights).toHaveBeenCalled());

    expect(draftScoreWeights).toHaveBeenCalledWith("token", [
      { pillar: "Availability", percentage: 50 },
      { pillar: "ShareOfShelf", percentage: 30 },
      { pillar: "PriceCompliance", percentage: 20 },
    ]);
  });

  it("edits a draft in place rather than drafting another", async () => {
    const user = userEvent.setup();

    render(<ScoreWeights />);
    await ready();

    await user.click(screen.getByRole("button", { name: "Edit" }));

    expect(screen.getByText("Editing version 2")).toBeTruthy();

    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(setScoreWeights).toHaveBeenCalled());

    expect(setScoreWeights).toHaveBeenCalledWith("token", 2, expect.anything());
    expect(draftScoreWeights).not.toHaveBeenCalled();
  });

  it("starts a new version from a published one, carrying its numbers across", async () => {
    /*
     * The route forward from a frozen version, and the reason it is a *copy* rather than an empty
     * form: an administrator re-weighting is almost always adjusting one pillar, and making them
     * retype the other two is how a typo enters a published set.
     */
    const user = userEvent.setup();

    fetchScoreWeights.mockResolvedValue([weighting(1, true, 70, 10, 20)]);

    render(<ScoreWeights />);
    await ready();

    await user.click(screen.getByRole("button", { name: "Start a new version from this" }));

    expect((screen.getByLabelText("Availability") as HTMLInputElement).value).toBe("70");
    expect((screen.getByLabelText("Share of shelf") as HTMLInputElement).value).toBe("10");

    // A *new* version, not an edit of the published one — the whole point.
    expect(screen.getByText("New version", { selector: "h2" })).toBeTruthy();
  });

  it("names what cannot be undone before it publishes", async () => {
    /*
     * A confirmation that says what is irreversible is a decision; one that asks "are you sure" is a
     * speed bump. This is the only place in the product where a single click makes something
     * permanent, so it is the one place the warning has to be a sentence.
     */
    const user = userEvent.setup();

    render(<ScoreWeights />);
    await ready();

    await user.click(screen.getByRole("button", { name: "Publish" }));

    expect(screen.getByText("Publishing cannot be undone.")).toBeTruthy();
    expect(publishScoreWeights).not.toHaveBeenCalled();

    await user.click(screen.getByRole("button", { name: "Publish it" }));

    await waitFor(() => expect(publishScoreWeights).toHaveBeenCalledWith("token", 2));
  });

  it("lets the confirmation be backed out of", async () => {
    const user = userEvent.setup();

    render(<ScoreWeights />);
    await ready();

    await user.click(screen.getByRole("button", { name: "Publish" }));
    await user.click(screen.getByRole("button", { name: "Cancel" }));

    expect(screen.queryByText("Publishing cannot be undone.")).toBeNull();
    expect(publishScoreWeights).not.toHaveBeenCalled();
  });

  it("shows the server's refusal in the reader's language", async () => {
    // The 409 an administrator meets if they publish a version somebody else already published.
    // ADR-0012: the code is what the screen branches on, and the catalogue is what they read.
    const user = userEvent.setup();

    publishScoreWeights.mockRejectedValue(
      new ApiError(409, [
        { field: null, code: "config.weights.alreadyPublished", message: "Already published." },
      ]),
    );

    render(<ScoreWeights />);
    await ready();

    await user.click(screen.getByRole("button", { name: "Publish" }));
    await user.click(screen.getByRole("button", { name: "Publish it" }));

    expect((await screen.findByRole("alert")).textContent).toContain(
      "Publishing is one-way — start a new version to change the weights.",
    );
  });

  it("shows a reader no controls at all", async () => {
    // `config:read` without `config:write`. Not disabled controls — a reader is not a writer who
    // has been stopped, and a dead button explains nothing about why.
    signedIn(["config:read"]);

    render(<ScoreWeights />);

    expect(await screen.findByText("Version 2")).toBeTruthy();

    expect(screen.queryByRole("button", { name: "New version" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Edit" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Publish" })).toBeNull();
  });

  it("says what an empty tenant means rather than showing nothing", async () => {
    // "No weighting yet" is a state with a consequence — an audit has nothing to score against —
    // and a blank panel would leave an administrator wondering whether it failed to load.
    fetchScoreWeights.mockResolvedValue([]);

    render(<ScoreWeights />);

    expect(
      await screen.findByText(/an audit has nothing to score against/),
    ).toBeTruthy();
  });
});
