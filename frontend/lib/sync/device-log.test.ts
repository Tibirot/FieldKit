import "fake-indexeddb/auto";

import { beforeEach, describe, expect, it, vi } from "vitest";

import { FieldKitDatabase } from "@/lib/sync/db";
import {
  MAXIMUM_BUFFERED,
  MAXIMUM_DETAIL,
  flushDeviceEvents,
  recordDeviceEvent,
} from "@/lib/sync/device-log";

const apiSend = vi.hoisted(() => vi.fn());

vi.mock("@/lib/api/client", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/client")>()),
  apiSend: (...args: unknown[]) => apiSend(...args),
}));

/**
 * What a device says when it is failing quietly (`observability §5`) — W13 slice 8.
 *
 * The property that matters is not that an event is recorded — it is that a device which **cannot
 * reach the server** keeps its report until it can. The interval where a device is broken is the
 * interval where it cannot tell anybody, so a buffer that cleared optimistically would lose exactly
 * the evidence it exists to carry.
 */
describe("the device log", () => {
  let db: FieldKitDatabase;

  beforeEach(async () => {
    vi.clearAllMocks();

    db = new FieldKitDatabase(`fieldkit:test:${crypto.randomUUID()}`);
    await db.open();
  });

  it("keeps what it recorded until the server has it", async () => {
    /*
     * The whole design in one test. A flush that throws leaves the buffer alone; the next one sends
     * the same events. A device offline for a week reports the week.
     */
    await recordDeviceEvent(db, "StorageEvicted", "the browser discarded the store");

    apiSend.mockRejectedValueOnce(new Error("no signal"));

    expect(await flushDeviceEvents(db, "token", "device-1")).toBe(0);

    apiSend.mockResolvedValueOnce({ accepted: 1 });

    expect(await flushDeviceEvents(db, "token", "device-1")).toBe(1);

    // And only once: delivered is forgotten, so a reconnect does not re-report a month of history.
    expect(await flushDeviceEvents(db, "token", "device-1")).toBe(0);
  });

  it("sends the events under the device that saw them", async () => {
    await recordDeviceEvent(db, "SyncFailed", "sync.push.deviceUnknown");

    apiSend.mockResolvedValueOnce({ accepted: 1 });

    await flushDeviceEvents(db, "token", "device-1");

    const [, path, , body] = apiSend.mock.calls[0] as [string, string, string, {
      deviceId: string;
      events: { kind: string; detail?: string; occurredAtUtc: string }[];
    }];

    expect(path).toBe("/api/sync/telemetry");
    expect(body.deviceId).toBe("device-1");
    expect(body.events[0].kind).toBe("SyncFailed");
    expect(body.events[0].detail).toBe("sync.push.deviceUnknown");
  });

  it("keeps the oldest when the buffer fills, not the newest", async () => {
    /*
     * The opposite of the usual ring buffer, and deliberate: the **first** failure explains the
     * rest. A store that was evicted and then threw a hundred quota errors should report the
     * eviction — keeping the newest would report a hundred symptoms and lose the cause.
     */
    await recordDeviceEvent(db, "StorageEvicted", "the cause");

    for (let extra = 0; extra < MAXIMUM_BUFFERED + 10; extra++)
      await recordDeviceEvent(db, "StorageQuotaExceeded", `symptom ${extra}`);

    apiSend.mockResolvedValueOnce({ accepted: MAXIMUM_BUFFERED });

    await flushDeviceEvents(db, "token", "device-1");

    const [, , , body] = apiSend.mock.calls[0] as [string, string, string, {
      events: { kind: string; detail?: string }[];
    }];

    expect(body.events).toHaveLength(MAXIMUM_BUFFERED);
    expect(body.events[0].detail).toBe("the cause");
  });

  it("caps what one event may say", async () => {
    // Mirrors the server's limit, so a device never sends a batch the server will truncate — and
    // never a stack trace, which from a minified bundle names no source this server could read.
    await recordDeviceEvent(db, "UnhandledError", "x".repeat(MAXIMUM_DETAIL * 2));

    apiSend.mockResolvedValueOnce({ accepted: 1 });

    await flushDeviceEvents(db, "token", "device-1");

    const [, , , body] = apiSend.mock.calls[0] as [string, string, string, {
      events: { detail?: string }[];
    }];

    expect(body.events[0].detail).toHaveLength(MAXIMUM_DETAIL);
  });

  it("records nothing rather than throwing when the store will not take it", async () => {
    /*
     * This runs from an error handler, often while something else is already failing — and the
     * device most likely to fail a write is the one whose storage is full, which is one of the
     * events being recorded. A rejection here would replace a diagnosable problem with an
     * undiagnosable one.
     */
    vi.spyOn(db.meta, "put").mockRejectedValueOnce(new Error("QuotaExceededError"));

    await expect(recordDeviceEvent(db, "StorageQuotaExceeded")).resolves.toBeUndefined();
  });

  it("says nothing when there is nothing to say", async () => {
    // No request at all on an ordinary sync, which is every sync. A device in good health should
    // cost one round trip fewer than a device in trouble.
    expect(await flushDeviceEvents(db, "token", "device-1")).toBe(0);
    expect(apiSend).not.toHaveBeenCalled();
  });

  it("carries no location, because there is nowhere to put one", async () => {
    /*
     * `security §4` and `observability §5`: check-in is the only geolocation this product captures.
     * The guarantee is structural rather than a rule somebody remembers — the event has three
     * fields — and this asserts the shape rather than the absence of one word, so a field added in
     * a hurry fails here rather than shipping.
     */
    await recordDeviceEvent(db, "UnhandledError", "boom");

    apiSend.mockResolvedValueOnce({ accepted: 1 });

    await flushDeviceEvents(db, "token", "device-1");

    const [, , , body] = apiSend.mock.calls[0] as [string, string, string, {
      events: Record<string, unknown>[];
    }];

    expect(Object.keys(body.events[0]).sort()).toEqual(["detail", "kind", "occurredAtUtc"]);
  });
});
