import { apiSend } from "@/lib/api/client";

import type {
  ReferenceAssortmentLine,
  ReferenceAssortmentOverride,
  ReferenceOutlet,
  ReferencePriceAssignment,
  ReferencePriceLine,
  ReferencePriceList,
  ReferencePromotion,
  ReferencePromotionAssignment,
  ReferencePlannedVisit,
  ReferenceProduct,
  ReferenceVisitWorkflow,
} from "@/lib/sync/db";
import type { EntityChanges } from "@/lib/sync/reference";

/** A device bound to the signed-in rep (`OFF-12`). */
export type Device = {
  id: string;
  userId: string;
  name: string | null;
  boundAtUtc: string;
  isActive: boolean;
  deactivatedBecause: string | null;
  deactivatedAtUtc: string | null;
};

/**
 * Registers this browser as the rep's device.
 *
 * There is no user id in the body, deliberately — the device belongs to the subject in the token, so
 * one cannot be supplied. Binding a second device deactivates the first as `Swapped`, which keeps
 * its right to one final drain-push (`A8`).
 */
export function bindDevice(accessToken: string, name: string | null): Promise<Device> {
  return apiSend<Device>("POST", "/api/sync/devices", accessToken, { name });
}

/** What the device tells the server it already has. Absent means "I have nothing" (sync engine §3). */
export type PullCursors = {
  outlets?: number;
  journeys?: number;
  configuration?: number;
  products?: number;
  assortment?: number;
  outletAssortment?: number;
  priceLists?: number;
  priceLines?: number;
  priceAssignments?: number;
  promotions?: number;
  promotionAssignments?: number;
};

export type PullResponse = {
  changes: {
    outlets: EntityChanges<ReferenceOutlet>;
    journeys: EntityChanges<ReferencePlannedVisit>;
    configuration: EntityChanges<ReferenceVisitWorkflow>;
    products: EntityChanges<ReferenceProduct>;
    assortment: EntityChanges<ReferenceAssortmentLine>;
    outletAssortment: EntityChanges<ReferenceAssortmentOverride>;
    priceLists: EntityChanges<ReferencePriceList>;
    priceLines: EntityChanges<ReferencePriceLine>;
    priceAssignments: EntityChanges<ReferencePriceAssignment>;
    promotions: EntityChanges<ReferencePromotion>;
    promotionAssignments: EntityChanges<ReferencePromotionAssignment>;
  };
  snapshotVersion: string;
};

/** Asks for everything that changed in the rep's territory since these cursors (`OFF-03`). */
export function pull(
  accessToken: string,
  deviceId: string,
  cursors: PullCursors,
  signal?: AbortSignal,
): Promise<PullResponse> {
  return apiSend<PullResponse>("POST", "/api/sync/pull", accessToken, { deviceId, cursors }, signal);
}

/**
 * One mutation on the wire.
 *
 * A typed property per kind rather than a `payload` blob — orders and audits will each add their own
 * beside `visit`, which is additive (sync engine §4). The outbox stores `payload` generically
 * because it does not care what is in it; this is where it becomes a named thing again.
 */
export type PushedMutation = { mutationId: string; type: string; visit?: unknown };

export type MutationResult = {
  mutationId: string;
  status: "accepted" | "rejected";
  reason: string | null;
  detail: string | null;
};

export type PushResponse = { results: MutationResult[] };

/** Drains a batch of captured work. Partial success is the normal answer (`OFF-04`, `OFF-09`). */
export function push(
  accessToken: string,
  deviceId: string,
  mutations: PushedMutation[],
  signal?: AbortSignal,
): Promise<PushResponse> {
  return apiSend<PushResponse>(
    "POST",
    "/api/sync/push",
    accessToken,
    { deviceId, mutations },
    signal,
  );
}
