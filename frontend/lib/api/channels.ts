import { apiDelete, apiGet, apiSend } from "@/lib/api/client";

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

export type ChannelWrite = { name: string };

export function createChannel(accessToken: string, channel: ChannelWrite): Promise<Channel> {
  return apiSend<Channel>("POST", "/api/outlets/channels", accessToken, channel);
}

export function updateChannel(
  accessToken: string,
  id: string,
  channel: ChannelWrite,
): Promise<Channel> {
  return apiSend<Channel>("PUT", `/api/outlets/channels/${id}`, accessToken, channel);
}

/**
 * Removes a channel.
 *
 * Refused while any outlet is classified as it. `BR-OUT-1` says every outlet has a channel, so there
 * is no such thing as removing one from underneath the outlets using it — and a channel drives
 * assortment, pricing and the visit workflow, so a silent reclassification would change what a rep
 * may sell there.
 */
export function deleteChannel(accessToken: string, id: string): Promise<void> {
  return apiDelete(`/api/outlets/channels/${id}`, accessToken);
}
