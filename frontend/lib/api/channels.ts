import { apiGet } from "@/lib/api/client";

/** A trade classification, as the filter needs it (`OUT-03`). */
export type Channel = {
  id: string;
  name: string;
};

export function fetchChannels(accessToken: string, signal?: AbortSignal): Promise<Channel[]> {
  return apiGet<Channel[]>("/api/outlets/channels", accessToken, signal);
}

/**
 * Channels are reference data, so this is cached per signed-in subject like everything else.
 *
 * Unpaged, deliberately: a tenant has tens of channels and the filter needs all of them at once to
 * be a filter at all. If that ever stops being true it will be because someone imported a channel
 * per outlet, which is a data problem rather than a paging one.
 */
export const channelsKey = (subject: string) => ["channels", subject] as const;
