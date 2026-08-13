// @vitest-environment jsdom

import "fake-indexeddb/auto";

import { screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { DeviceStatus } from "@/components/field/device-status";
import type { SyncContextValue } from "@/components/sync/sync-provider";
import { closeDatabase, FieldKitDatabase } from "@/lib/sync/db";
import { render } from "@/test/render";

/**
 * What this device is holding, and what the browser will let it keep (`OFF-05`, `OFF-11`) — the
 * storage half arrives in W9 slice 11.
 *
 * The warnings are the part worth testing, and specifically the cases where the screen should say
 * **nothing**: a rep who is warned about something they cannot act on learns to scroll past the
 * banner, and then misses the one that mattered.
 */
const sync = vi.hoisted(() => ({ current: {} as SyncContextValue }));

vi.mock("@/components/sync/sync-provider", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/components/sync/sync-provider")>()),
  useSync: () => sync.current,
}));

function withStorage(implementation: Partial<StorageManager> | undefined) {
  Object.defineProperty(globalThis.navigator, "storage", {
    value: implementation,
    configurable: true,
  });
}

const MEGABYTE = 1024 * 1024;

let db: FieldKitDatabase;

beforeEach(() => {
  db = new FieldKitDatabase(`device:${crypto.randomUUID()}`);
  sync.current = { db, pending: 0, failed: 0, photographs: 0, running: false, outcome: null, syncNow: vi.fn() };

  // Chromium fires `beforeinstallprompt`; jsdom never does, so the install offer stays absent here
  // and has its own file.
  Object.defineProperty(globalThis, "matchMedia", {
    configurable: true,
    value: () => ({ matches: false }),
  });
});

afterEach(async () => {
  await db.delete();
  closeDatabase();
});

describe("<DeviceStatus> and the storage it reports", () => {
  it("shows what is used out of what is allowed", async () => {
    withStorage({
      estimate: async () => ({ usage: 18 * MEGABYTE, quota: 2048 * MEGABYTE }),
      persisted: async () => true,
    });

    render(<DeviceStatus />);

    expect(await screen.findByText("18 of 2048 MB")).toBeTruthy();
  });

  it("says so rather than showing a zero when the browser will not answer", async () => {
    // Private modes reject; older engines have no API. "0 MB" would read as an empty device, which
    // is a different and more alarming claim than "not reported".
    withStorage(undefined);

    render(<DeviceStatus />);

    expect(await screen.findByText("Not reported")).toBeTruthy();
  });

  it("warns about a device nearly out of room", async () => {
    withStorage({
      estimate: async () => ({ usage: 95 * MEGABYTE, quota: 100 * MEGABYTE }),
      persisted: async () => true,
    });

    render(<DeviceStatus />);

    expect(await screen.findByRole("alert")).toHaveProperty(
      "textContent",
      expect.stringContaining("nearly out of space"),
    );
  });

  it("warns about eviction only when there is unsent work", async () => {
    sync.current = { ...sync.current, pending: 3 };
    withStorage({
      estimate: async () => ({ usage: MEGABYTE, quota: 2048 * MEGABYTE }),
      persisted: async () => false,
    });

    render(<DeviceStatus />);

    expect(await screen.findByRole("alert")).toHaveProperty(
      "textContent",
      expect.stringContaining("has not promised to keep"),
    );
  });

  it("says nothing about eviction when the outbox is empty", async () => {
    withStorage({
      estimate: async () => ({ usage: MEGABYTE, quota: 2048 * MEGABYTE }),
      persisted: async () => false,
    });

    render(<DeviceStatus />);

    // The figures still render, so this is the warning being absent rather than the screen failing.
    await screen.findByText("1 of 2048 MB");

    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("says nothing at all when the browser never offered an opinion", async () => {
    sync.current = { ...sync.current, pending: 5 };
    withStorage({ estimate: async () => ({ usage: MEGABYTE, quota: 2048 * MEGABYTE }) });

    render(<DeviceStatus />);

    await screen.findByText("1 of 2048 MB");

    expect(screen.queryByRole("alert")).toBeNull();
  });
});
