"use client";

import { useQuery } from "@tanstack/react-query";

import { useAuth } from "@/components/auth-provider";
import { fetchIdentity } from "@/lib/api/identity";

export const identityKey = (subject: string) => ["identity", subject] as const;

/**
 * What the signed-in user may do, as the API derives it.
 *
 * **From `/api/auth/whoami`, never from the token in the browser.** The realms keep `permissions`
 * off the ID token on purpose, so decoding it locally would yield an empty list rather than fail —
 * and a second opinion about authorization that cannot enforce anything is worse than none. The API
 * re-derives both tenant and permissions from the token it validated (ADR-0008); that is the only
 * copy that decides anything, and this asks it.
 *
 * **What this is for is the screen, not the rule.** Every endpoint still checks. Hiding a control is
 * about not offering someone a door that will not open — a "New user" button on a page that has just
 * refused to show them users, which is what the Phase 1 demo walk actually found.
 */
export function usePermissions(): {
  /** Whether the caller holds every one of `required`. Unknown counts as no. */
  has: (...required: string[]) => boolean;
  /** Whether the caller holds at least one of `required`. Unknown counts as no. */
  hasAny: (...required: string[]) => boolean;
  /** True until the answer arrives. Nothing gated is offered before then. */
  isPending: boolean;
} {
  const { user } = useAuth();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;

  const identity = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey: identityKey(subject ?? ""),
    queryFn: ({ signal }) => fetchIdentity(accessToken!, signal),

    // Permissions change when an admin edits a role, which does not happen mid-session for the
    // person it affects — and the token they are holding carries the old ones until it refreshes
    // anyway. Re-asking on every screen would be a request per navigation for an answer that is
    // constant for the session.
    staleTime: Infinity,
  });

  const granted = new Set(identity.data?.permissions ?? []);

  return {
    // Pending counts as denied, so nothing gated is rendered before the answer arrives. The flash of
    // a control appearing a moment late is the harmless direction; a control that appears and is
    // then taken away is the one that reads as a bug — or worse, gets clicked first.
    has: (...required) => required.every((permission) => granted.has(permission)),
    hasAny: (...required) => required.some((permission) => granted.has(permission)),
    isPending: identity.isPending,
  };
}
