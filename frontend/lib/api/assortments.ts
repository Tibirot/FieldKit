import { apiGet, apiSend } from "@/lib/api/client";

/** One product in a channel's assortment, and whether it must be stocked (`PRD-02`). */
export type AssortmentItem = {
  productId: string;
  sku: string;
  name: string;
  mustStock: boolean;
};

/** One line as an author sets it. */
export type AssortmentLine = { productId: string; mustStock: boolean };

export function fetchChannelAssortment(
  accessToken: string,
  channelId: string,
  signal?: AbortSignal,
): Promise<AssortmentItem[]> {
  return apiGet<AssortmentItem[]>(
    `/api/products/assortments/channels/${channelId}`,
    accessToken,
    signal,
  );
}

/**
 * Replaces a channel's assortment wholesale.
 *
 * The API's PUT is a replace, not a merge — which is what makes the editor a picture of the whole
 * catalogue rather than a queue of add/remove operations. A partial save would need the client to
 * know what it had not sent, and two people editing one channel would interleave silently.
 */
export function setChannelAssortment(
  accessToken: string,
  channelId: string,
  items: readonly AssortmentLine[],
): Promise<AssortmentItem[]> {
  return apiSend<AssortmentItem[]>(
    "PUT",
    `/api/products/assortments/channels/${channelId}`,
    accessToken,
    { items },
  );
}

/**
 * Cached per channel as well as per subject.
 *
 * Two channels' assortments are different answers to the same question, so they cannot share a key —
 * and the prefix is what lets a save invalidate every channel at once without listing them.
 */
export const channelAssortmentKey = (subject: string, channelId: string) =>
  ["assortment", subject, channelId] as const;

// ── Per-outlet overrides ───────────────────────────────────────────────────────────────────────

/**
 * How one outlet departs from its channel (`PRD-02`).
 *
 * `Added` covers two cases the API treats as one: a product the channel does not carry, and a
 * product it does carry whose must-stock flag this shop disagrees about. `Removed` is only
 * meaningful for something the channel carries — there is nothing to take away otherwise.
 */
export type OverrideKind = "Added" | "Removed";

export type AssortmentOverride = {
  productId: string;
  sku: string;
  name: string;
  kind: OverrideKind;
  mustStock: boolean;
};

/** One override as an author sets it. */
export type OverrideLine = { productId: string; kind: OverrideKind; mustStock: boolean };

/**
 * What this outlet actually sells — its channel's assortment, minus its removals, plus its
 * additions.
 *
 * **Read from the server rather than computed here**, even though the client holds all three
 * inputs. That rule is `PRD-02`'s and it lives in one place; a second implementation in TypeScript
 * would be a copy to keep in step, and the copy would be the one nobody notices drifting.
 */
export function fetchOutletAssortment(
  accessToken: string,
  outletId: string,
  signal?: AbortSignal,
): Promise<AssortmentItem[]> {
  return apiGet<AssortmentItem[]>(
    `/api/products/assortments/outlets/${outletId}`,
    accessToken,
    signal,
  );
}

export function fetchOutletOverrides(
  accessToken: string,
  outletId: string,
  signal?: AbortSignal,
): Promise<AssortmentOverride[]> {
  return apiGet<AssortmentOverride[]>(
    `/api/products/assortments/outlets/${outletId}/overrides`,
    accessToken,
    signal,
  );
}

/** Replaces this outlet's overrides wholesale, like every other set in this module. */
export function setOutletOverrides(
  accessToken: string,
  outletId: string,
  overrides: readonly OverrideLine[],
): Promise<AssortmentOverride[]> {
  return apiSend<AssortmentOverride[]>(
    "PUT",
    `/api/products/assortments/outlets/${outletId}/overrides`,
    accessToken,
    { overrides },
  );
}

export const outletAssortmentKey = (subject: string, outletId: string) =>
  ["assortment", subject, "outlet", outletId] as const;

export const outletOverridesKey = (subject: string, outletId: string) =>
  ["assortment", subject, "overrides", outletId] as const;
