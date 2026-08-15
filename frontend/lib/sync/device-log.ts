import { apiSend } from "@/lib/api/client";
import type { FieldKitDatabase } from "@/lib/sync/db";

/**
 * What a device can say went wrong, matching the server's `DeviceEventKind` — W13 slice 8.
 *
 * A closed vocabulary on both sides. The server refuses a kind it does not know rather than
 * bucketing it, so a typo here is a release that reports nothing rather than a release that reports
 * something unreadable — which is the failure worth having, because it is the loud one.
 */
export type DeviceEventKind =
  | "UnhandledError"
  | "ServiceWorkerFailure"
  | "StorageQuotaExceeded"
  | "StorageEvicted"
  | "SyncFailed";

export type DeviceEvent = {
  kind: DeviceEventKind;
  occurredAtUtc: string;
  detail?: string;
};

/**
 * How many events the device keeps. The server takes fifty in a batch; keeping the same number means
 * a full buffer is exactly one batch and there is no second rule about splitting.
 */
export const MAXIMUM_BUFFERED = 50;

/** What one event may say, mirroring `TelemetryEndpoints.MaximumDetail`. */
export const MAXIMUM_DETAIL = 500;

/**
 * Where the buffer lives.
 *
 * **A `meta` row rather than an object store**, and that is a considered trade. A store of its own
 * would mean a Dexie version bump and a migration path (`OFF-13`) for data that is thrown away the
 * moment it is delivered — diagnostics, not a record of work. The buffer is capped at fifty small
 * objects, so a single JSON row is the right size of thing, and flushing is one delete rather than a
 * range scan.
 */
const KEY = "deviceEvents";

/**
 * Records something that went wrong, for the next reconnect to carry
 * ([observability §5](../../../docs/architecture/15-observability.md#5-client-side-field-app)).
 *
 * **Never throws, and never awaits anything the caller needs.** This is called from an error handler
 * — often while something else is already failing — so a rejection here would replace a diagnosable
 * problem with an undiagnosable one. A device whose storage is full cannot record that its storage is
 * full; that is a real limit and the reason the counter on the server matters more than any single
 * event.
 *
 * **No location, ever** ([security §4](../../../docs/architecture/16-security.md)). There is nowhere
 * to put one: `DeviceEvent` has three fields and the server's record has the same three. The check-in
 * point is the only geolocation this product captures, and it is captured because a supervisor reads
 * it — not because a client can.
 */
export async function recordDeviceEvent(
  db: FieldKitDatabase,
  kind: DeviceEventKind,
  detail?: string,
): Promise<void> {
  try {
    const buffered = await read(db);

    const event: DeviceEvent = {
      kind,
      occurredAtUtc: new Date().toISOString(),
      detail: detail?.slice(0, MAXIMUM_DETAIL),
    };

    /*
     * The **oldest** are kept when the buffer is full, and the newest dropped.
     *
     * That is the opposite of the usual ring buffer and it is deliberate: the first failure is the
     * one that explains the rest. A store that evicted itself, then threw a hundred quota errors,
     * should report the eviction — keeping the newest would report a hundred symptoms and lose the
     * cause.
     */
    const kept = [...buffered, event].slice(0, MAXIMUM_BUFFERED);

    await db.meta.put({ key: KEY, value: JSON.stringify(kept) });
  } catch {
    // Deliberately silent. See above: this runs while something else is already going wrong.
  }
}

/**
 * Sends what the device has been holding, and forgets it.
 *
 * **Cleared only after the server has it.** A failed flush leaves the buffer alone, so a device that
 * has been offline for a week still reports the week — which is the entire point, since the interval
 * where a device is failing is the interval where it cannot tell anybody.
 *
 * **And a flush that fails is not recorded as a failure.** Doing so would make an unreachable server
 * generate an event per attempt, which is a device filling its own buffer with the news that it
 * cannot empty it.
 */
export async function flushDeviceEvents(
  db: FieldKitDatabase,
  accessToken: string,
  deviceId: string,
  signal?: AbortSignal,
): Promise<number> {
  const buffered = await read(db);

  if (buffered.length === 0) return 0;

  try {
    await apiSend<unknown>(
      "POST",
      "/api/sync/telemetry",
      accessToken,
      { deviceId, events: buffered },
      signal,
    );
  } catch {
    return 0;
  }

  await db.meta.delete(KEY);

  return buffered.length;
}

/** What is waiting, or nothing at all if the row is missing or unreadable. */
async function read(db: FieldKitDatabase): Promise<DeviceEvent[]> {
  const row = await db.meta.get(KEY);

  if (!row) return [];

  try {
    const parsed: unknown = JSON.parse(row.value);

    // A row written by an older version, or half-written by an eviction, is not worth a crash in the
    // code whose job is to report crashes.
    return Array.isArray(parsed) ? (parsed as DeviceEvent[]) : [];
  } catch {
    return [];
  }
}
