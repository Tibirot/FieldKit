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
