// @vitest-environment jsdom

import "fake-indexeddb/auto";

import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue, AuthStatus } from "@/components/auth-provider";
import { FieldShell } from "@/components/field/shell";
import { ApiError } from "@/lib/api/client";
import { closeDatabase, openDatabase } from "@/lib/sync/db";
import { enqueue } from "@/lib/sync/outbox";
import { render } from "@/test/render";

const replace = vi.fn();

vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children }: { href: string; children: React.ReactNode }) => (
    <a href={href}>{children}</a>
  ),
  usePathname: () => "/field",
  useRouter: () => ({ replace }),
}));

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

const api = vi.hoisted(() => ({
  bindDevice: vi.fn(),
  pull: vi.fn(),
  push: vi.fn(),
}));

vi.mock("@/lib/api/sync", () => api);

const WORKSPACE = "fieldkit-dev";
const SUBJECT = "subject-a";

function session(status: AuthStatus): AuthContextValue {
  return {
    status,
    user:
      status === "authenticated"
        ? ({ access_token: "token", profile: { sub: SUBJECT } } as AuthContextValue["user"])
        : null,
    workspace: WORKSPACE,
    signIn: vi.fn(),
    signOut: vi.fn(),
    completeSignIn: vi.fn(),
    expire: vi.fn(),
    reauthenticate: vi.fn(),
  };
}

/** An empty round, so `startSync` finds nothing to do and nothing to complain about. */
function nothingToPull() {
  const empty = { upserts: [], tombstones: [], cursor: 0 };

  return {
    changes: {
      outlets: empty,
      journeys: empty,
      configuration: empty,
      products: empty,
      assortment: empty,
      outletAssortment: empty,
      priceLists: empty,
      priceLines: empty,
      priceAssignments: empty,
      promotions: empty,
      promotionAssignments: empty,
    },
    snapshotVersion: "outlets#0",
  };
}

beforeEach(() => {
  replace.mockClear();
  api.bindDevice.mockReset().mockResolvedValue({ id: "device-1", name: "Browser" });
  api.pull.mockReset().mockResolvedValue(nothingToPull());
  api.push.mockReset().mockResolvedValue({ results: [] });
  auth.current = session("authenticated");
});

afterEach(async () => {
  const db = openDatabase(WORKSPACE, SUBJECT);
  await db.delete();
  closeDatabase();
});

describe("<FieldShell>", () => {
  it("binds this browser once and lets the rep in", async () => {
    render(
      <FieldShell>
        <p>Today&apos;s journey</p>
      </FieldShell>,
    );

    expect(await screen.findByText("Today's journey")).toBeTruthy();
    expect(api.bindDevice).toHaveBeenCalledTimes(1);

    // Stored, so the next launch is the offline case below rather than another bind — a rebind on
    // every start would deactivate the previous registration as a swap, every start.
    const db = openDatabase(WORKSPACE, SUBJECT);
    expect(await db.meta.get("deviceId")).toEqual({ key: "deviceId", value: "device-1" });
  });

  it("opens with no network at all once the device is already bound", async () => {
    // The claim the whole route group exists for. A rep starting their day in a basement has a
    // device id in the local store and no way to reach the server; the app has to open anyway.
    const db = openDatabase(WORKSPACE, SUBJECT);
    await db.meta.put({ key: "deviceId", value: "device-1" });

    api.bindDevice.mockRejectedValue(new TypeError("Failed to fetch"));
    api.pull.mockRejectedValue(new TypeError("Failed to fetch"));
    api.push.mockRejectedValue(new TypeError("Failed to fetch"));

    render(
      <FieldShell>
        <p>Today&apos;s journey</p>
      </FieldShell>,
    );

    expect(await screen.findByText("Today's journey")).toBeTruthy();

    // And it did not try. `ensureDevice` answers from the store, so an offline start makes no
    // request that could fail — the test would pass on a shell that called and swallowed the error,
    // which is a different and worse design.
    expect(api.bindDevice).not.toHaveBeenCalled();
  });

  it("says so when a device has never been bound and there is no signal to bind with", async () => {
    // The one state the field app genuinely cannot reach offline: an id is minted by the server, and
    // until there is one there is nothing to pull into either. Worth its own screen rather than a
    // spinner, because the fix is a thing the rep can do.
    api.bindDevice.mockRejectedValue(new TypeError("Failed to fetch"));

    render(
      <FieldShell>
        <p>Today&apos;s journey</p>
      </FieldShell>,
    );

    expect(await screen.findByText("This device needs a connection once")).toBeTruthy();
    expect(screen.queryByText("Today's journey")).toBeNull();

    api.bindDevice.mockResolvedValue({ id: "device-1", name: "Browser" });
    await userEvent.click(screen.getByRole("button", { name: "Try again" }));

    expect(await screen.findByText("Today's journey")).toBeTruthy();
  });

  it("replaces the app when this device is no longer the rep's, rather than warning over it", async () => {
    // A rejected device cannot pull, so everything below is frozen at the last sync. A banner would
    // leave a rep working a stale journey, which is the failure this state exists to prevent.
    const db = openDatabase(WORKSPACE, SUBJECT);
    await db.meta.put({ key: "deviceId", value: "device-1" });
    await db.watermarks.put({ entity: "products", cursor: 41 });

    // The status is what `classify` reads; the code rides along in the problems array, where the
    // rest of the app finds it. An earlier version passed it as a third argument that `ApiError`
    // does not take — harmless at runtime, and a lie about the shape.
    api.pull.mockRejectedValue(
      new ApiError(409, [{ field: null, code: "sync.pull.deviceInactive", message: "Rebind." }]),
    );

    render(
      <FieldShell>
        <p>Today&apos;s journey</p>
      </FieldShell>,
    );

    // The manager runs on mount, so the rejection arrives without anyone pressing anything.
    expect(await screen.findByText("This device is no longer the active one")).toBeTruthy();
    expect(screen.queryByText("Today's journey")).toBeNull();

    api.pull.mockResolvedValue(nothingToPull());
    api.bindDevice.mockResolvedValue({ id: "device-2", name: "Browser" });

    await userEvent.click(screen.getByRole("button", { name: "Register this device again" }));

    await waitFor(async () =>
      expect(await db.meta.get("deviceId")).toEqual({ key: "deviceId", value: "device-2" }),
    );

    // The watermarks survive, and that is deliberate: the server keys a device's recorded scope by
    // device id, so a new id has no scope and re-baselines the territory on its own. Clearing them
    // would re-download the catalogue to solve a problem the server has already solved.
    expect(await db.watermarks.get("products")).toEqual({ entity: "products", cursor: 41 });
  });

  it("promises a rep with an empty outbox that nothing is lost, without counting nothing at them", async () => {
    // Found by reading the live screen, not the catalogue: the sentence named the pending count
    // unconditionally, so a rejected device with an empty outbox said "including the no items
    // waiting to be sent". The reassurance is the point of the screen, and it was the part that
    // came out broken.
    const db = openDatabase(WORKSPACE, SUBJECT);
    await db.meta.put({ key: "deviceId", value: "device-1" });

    api.pull.mockRejectedValue(
      new ApiError(409, [{ field: null, code: "sync.pull.deviceInactive", message: "Rebind." }]),
    );

    render(
      <FieldShell>
        <p>Today&apos;s journey</p>
      </FieldShell>,
    );

    expect(
      await screen.findByText(
        "Your account was set up on another device, so this one has stopped syncing. Register it again to carry on — nothing captured here is lost.",
      ),
    ).toBeTruthy();
  });

  it("counts the work still queued, because that is what the rep is afraid of losing", async () => {
    const db = openDatabase(WORKSPACE, SUBJECT);
    await db.meta.put({ key: "deviceId", value: "device-1" });
    await enqueue(db, { type: "CapturedVisit", subjectId: "visit-1", payload: {} });

    // The *push* is what gets refused here, which is the order it happens in: a device replaced
    // overnight is told so by the first attempt to hand over the day's work. The batch goes back to
    // `pending`, so the count the screen quotes is the work still on the phone.
    api.push.mockRejectedValue(
      new ApiError(409, [{ field: null, code: "sync.push.deviceInactive", message: "Rebind." }]),
    );

    render(
      <FieldShell>
        <p>Today&apos;s journey</p>
      </FieldShell>,
    );

    expect(
      await screen.findByText(
        "Your account was set up on another device, so this one has stopped syncing. Register it again to carry on — nothing captured here is lost, including the one item waiting to be sent.",
      ),
    ).toBeTruthy();
  });

  it("hands the session states to the guard rather than answering them itself", async () => {
    // The extraction, asserted from this side. An expired session in the field app has to ask the
    // same question the back office asks — a rep dumped to `/login` mid-visit reads as the app
    // losing their work.
    auth.current = session("expired");

    render(
      <FieldShell>
        <p>Today&apos;s journey</p>
      </FieldShell>,
    );

    expect(await screen.findByText("Your session has expired")).toBeTruthy();
    expect(screen.queryByText("Today's journey")).toBeNull();
  });
});
