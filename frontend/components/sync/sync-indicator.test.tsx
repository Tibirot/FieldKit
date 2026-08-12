/**
 * @vitest-environment jsdom
 */
import "fake-indexeddb/auto";

import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { NextIntlClientProvider } from "next-intl";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { SyncBadge } from "@/components/sync/sync-badge";
import { SyncIndicator } from "@/components/sync/sync-indicator";
import { SyncProvider } from "@/components/sync/sync-provider";
import messages from "@/messages/en.json";
import { closeDatabase, openDatabase } from "@/lib/sync/db";
import { enqueue, markRejected } from "@/lib/sync/outbox";

/**
 * The connectivity indicator, *Sync now*, and per-item badges (`OFF-05`, `OFF-06`, W8 slice 13).
 *
 * Rendered against the **real** provider, the real Dexie store and the real message catalogue —
 * only the network is faked. A test that stubbed `useSync` would assert that a chip renders the
 * string it was handed, which is not a claim anybody needs; what these check is that the states are
 * ranked correctly and that the count is *live*, which is the whole point of the component.
 */
const api = vi.hoisted(() => ({
  bindDevice: vi.fn(),
  pull: vi.fn(),
  push: vi.fn(),
}));

vi.mock("@/lib/api/sync", () => api);

const auth = vi.hoisted(() => ({ token: "token" }));

vi.mock("@/components/auth-provider", () => ({
  useAuth: () => ({ user: { access_token: auth.token } }),
}));

const TENANT = "acme";
const SUBJECT = `subject-${crypto.randomUUID()}`;

function show(children: React.ReactNode) {
  return render(
    <NextIntlClientProvider locale="en" messages={messages}>
      <SyncProvider tenant={TENANT} subject={SUBJECT} deviceId="device-1">
        {children}
      </SyncProvider>
    </NextIntlClientProvider>,
  );
}

/** A pull that changes nothing, so the manager's runs are only about the outbox. */
const QUIET_PULL = {
  changes: {
    outlets: { upserts: [], tombstones: [], cursor: 0 },
    journeys: { upserts: [], tombstones: [], cursor: 0 },
    configuration: { upserts: [], tombstones: [], cursor: 0 },
    products: { upserts: [], tombstones: [], cursor: 0 },
    assortment: { upserts: [], tombstones: [], cursor: 0 },
    outletAssortment: { upserts: [], tombstones: [], cursor: 0 },
    priceLists: { upserts: [], tombstones: [], cursor: 0 },
    priceLines: { upserts: [], tombstones: [], cursor: 0 },
    priceAssignments: { upserts: [], tombstones: [], cursor: 0 },
    promotions: { upserts: [], tombstones: [], cursor: 0 },
    promotionAssignments: { upserts: [], tombstones: [], cursor: 0 },
  },
  snapshotVersion: "outlets#0",
};

beforeEach(() => {
  auth.token = "token";
  api.pull.mockReset().mockResolvedValue(QUIET_PULL);
  api.push.mockReset();
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true);
});

afterEach(async () => {
  const db = openDatabase(TENANT, SUBJECT);
  await db.outbox.clear();
  closeDatabase();
  vi.restoreAllMocks();
});

describe("the indicator", () => {
  it("says everything is synced rather than showing nothing", async () => {
    // An indicator that disappears when all is well cannot be told apart from one that is broken,
    // and a rep deciding whether they can close the app needs an answer rather than an absence.
    show(<SyncIndicator />);

    expect(await screen.findByText("Everything synced")).not.toBeNull();
  });

  it("counts unsent work, and the count is live", async () => {
    // The count moves for two unrelated reasons: the manager drains, and a screen enqueues. A
    // provider that only refreshed after syncing would show a stale zero for exactly the window
    // between capturing a visit and getting signal — the window this exists for.
    const db = openDatabase(TENANT, SUBJECT);

    show(<SyncIndicator />);
    await screen.findByText("Everything synced");

    await enqueue(db, { type: "CapturedVisit", subjectId: "visit-1", payload: {} });

    expect(await screen.findByText("1 item waiting to sync")).not.toBeNull();

    await enqueue(db, { type: "CapturedVisit", subjectId: "visit-2", payload: {} });

    expect(await screen.findByText("2 items waiting to sync")).not.toBeNull();
  });

  it("explains an offline device rather than reporting it as a problem with the work", async () => {
    vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);

    const db = openDatabase(TENANT, SUBJECT);
    await enqueue(db, { type: "CapturedVisit", subjectId: "visit-1", payload: {} });

    show(<SyncIndicator />);

    // Offline outranks the count: the rep is told the work is safe and why it has not gone yet,
    // rather than a number that reads like something is wrong with it.
    expect(await screen.findByText("Offline — your work is saved on this device")).not.toBeNull();
  });

  it("asks for a sign-in when that is what is wrong", async () => {
    // The four interruption reasons exist so this chip can say something a rep can act on. Told
    // "3 items waiting", they wait; told to sign in, they sign in.
    const db = openDatabase(TENANT, SUBJECT);
    await enqueue(db, { type: "CapturedVisit", subjectId: "visit-1", payload: {} });

    // No live session. The manager answers `unauthorized` without touching the network, which is
    // the real path — a signed-out device should not be generating a 401 per reconnect.
    auth.token = null as unknown as string;

    show(<SyncIndicator />);
    await userEvent.click(await screen.findByRole("button", { name: "Sync now" }));

    expect(await screen.findByText("Sign in again to sync")).not.toBeNull();
  });

  it("drains the outbox when Sync now is pressed", async () => {
    const db = openDatabase(TENANT, SUBJECT);
    const entry = await enqueue(db, { type: "CapturedVisit", subjectId: "visit-1", payload: {} });

    api.push.mockResolvedValue({
      results: [{ mutationId: entry.mutationId, status: "accepted", reason: null, detail: null }],
    });

    show(<SyncIndicator />);
    await screen.findByText("1 item waiting to sync");

    await userEvent.click(screen.getByRole("button", { name: "Sync now" }));

    expect(await screen.findByText("Everything synced")).not.toBeNull();
    expect(api.push).toHaveBeenCalledOnce();
  });

  it("offers Sync now even when the device believes it is offline", async () => {
    // `navigator.onLine` is a guess — true on a captive portal, false for a moment after a real
    // reconnect. A disabled button would make the app's wrong guess final.
    vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);

    show(<SyncIndicator />);

    const button = (await screen.findByRole("button", { name: "Sync now" })) as HTMLButtonElement;

    expect(button.disabled).toBe(false);
  });

  it("says refused work needs attention instead of calling it synced", async () => {
    /*
     * The bug W11 slice 8c fixes, from the screen. `pendingCount` counts only `pending`, so a
     * mutation the server refused was invisible to this chip — and a rep who had just lost an order
     * was told **"Everything synced"**. Found in a browser, because every test that could have
     * caught it mocked the refusal away.
     */
    const db = openDatabase(TENANT, SUBJECT);
    const entry = await enqueue(db, { type: "CapturedOrder", subjectId: "order-1", payload: {} });

    show(<SyncIndicator />);
    await screen.findByText("1 item waiting to sync");

    await markRejected(db, entry.mutationId, "order.ingest.visitUnknown");

    expect(
      await screen.findByText("1 item needs attention — it was refused and will not retry"),
    ).not.toBeNull();

    expect(screen.queryByText("Everything synced")).toBeNull();
  });

  it("ranks refused work above being offline", async () => {
    // Offline outranks a pending count because the count is not the rep's fault and clears itself.
    // A refusal is neither: it needs a person, and no amount of signal will change it (`OFF-09`).
    vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);

    const db = openDatabase(TENANT, SUBJECT);
    const entry = await enqueue(db, { type: "CapturedOrder", subjectId: "order-1", payload: {} });
    await markRejected(db, entry.mutationId, "order.ingest.visitUnknown");

    show(<SyncIndicator />);

    expect(
      await screen.findByText("1 item needs attention — it was refused and will not retry"),
    ).not.toBeNull();
  });
});

describe("a per-item badge", () => {
  it("shows nothing for work the server has answered for", async () => {
    // The opposite rule to the indicator's, and right for the opposite reason: a column of "synced"
    // against every row is noise a rep stops reading, and the one that says *failed* is lost in it.
    show(<SyncBadge subjectId="visit-1" />);

    expect(screen.queryByText("Not synced")).toBeNull();
    expect(screen.queryByText("Needs attention")).toBeNull();
  });

  it("marks a visit that has not gone yet", async () => {
    const db = openDatabase(TENANT, SUBJECT);
    await enqueue(db, { type: "CapturedVisit", subjectId: "visit-1", payload: {} });

    show(<SyncBadge subjectId="visit-1" />);

    expect(await screen.findByText("Not synced")).not.toBeNull();
  });

  it("marks a rejection as needing a person, not a retry", async () => {
    const db = openDatabase(TENANT, SUBJECT);
    const entry = await enqueue(db, { type: "CapturedVisit", subjectId: "visit-1", payload: {} });
    await markRejected(db, entry.mutationId, "visit.ingest.outletUnknown");

    show(<SyncBadge subjectId="visit-1" />);

    expect(await screen.findByText("Needs attention")).not.toBeNull();
  });

  it("is about one visit, not the outbox", async () => {
    // A badge that read the pending count would light up every row in the list the moment any one
    // of them was captured.
    const db = openDatabase(TENANT, SUBJECT);
    await enqueue(db, { type: "CapturedVisit", subjectId: "visit-1", payload: {} });

    show(<SyncBadge subjectId="visit-2" />);

    expect(screen.queryByText("Not synced")).toBeNull();
  });
});
