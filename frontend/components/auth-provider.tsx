"use client";

import type { User, UserManager } from "oidc-client-ts";
import { createContext, use, useCallback, useEffect, useMemo, useRef, useState } from "react";

import { createUserManager, type OidcSettings } from "@/lib/auth/oidc";
import { forgetSettings, readSettings, rememberSettings } from "@/lib/auth/settings-store";
import { forgetWorkspace, readWorkspace, rememberWorkspace } from "@/lib/auth/workspace";

/**
 * The signed-in session, for the whole app (`IAM-01`).
 *
 * `status` is four-valued rather than a boolean, and each split earns its place.
 *
 * "We have not looked yet" and "we looked and there is nobody" are different things: collapsing them
 * flashes the sign-in screen at an already signed-in user on every reload, which on a phone reads as
 * being logged out.
 *
 * `"expired"` and `"anonymous"` are the second split, and the one that was missing. Both mean "no
 * usable token", but they are different situations for the person holding the device. Anonymous is
 * someone who has not signed in — send them to sign-in. Expired is someone who *was* signed in and
 * whose session died underneath them — they know which workspace they belong to, they are probably
 * mid-task, and the app knows enough to offer them one button back. Treating expired as anonymous
 * throws away that context; treating it as authenticated is worse, and is what used to happen: the
 * shell stayed rendered, `/api/auth/whoami` answered 401, permissions came back empty, and every
 * gated control quietly disappeared. A UI that says "you may do nothing" when it means "prove who
 * you are again".
 */
export type AuthStatus = "loading" | "authenticated" | "expired" | "anonymous";

export type AuthContextValue = {
  status: AuthStatus;
  user: User | null;
  workspace: string | null;
  /** Starts the redirect to a workspace's realm. Needs a live Keycloak address, so it is passed one. */
  signIn: (workspace: string, settings: OidcSettings) => Promise<void>;
  signOut: () => Promise<void>;
  /** Completes the redirect back from Keycloak. Only the callback route calls this. */
  completeSignIn: (settings: OidcSettings) => Promise<void>;
  /**
   * Declares the session over without ending it at Keycloak.
   *
   * Called by whatever notices first — the token's own expiry event, or a 401 from the API. It is
   * idempotent and safe to call repeatedly, because both of those fire more than once: a screen with
   * four queries produces four 401s for one dead token.
   */
  expire: () => void;
  /**
   * Signs back in to the workspace already on the device, no typing.
   *
   * Returns `false` when it cannot — nothing remembered, or the stored Keycloak address is unusable
   * — which is the caller's cue to fall back to the sign-in screen.
   */
  reauthenticate: () => Promise<boolean>;
};

/**
 * Note what is deliberately *not* here: tenant and permissions.
 *
 * They are on the access token, and the realms keep `permissions` off the ID token on purpose — so
 * reading them from `user.profile` would quietly yield an empty list rather than fail. Ask the API
 * instead (`lib/api/identity.ts`): it re-derives both from the token it validated, which is the only
 * copy that decides anything.
 */
const AuthContext = createContext<AuthContextValue | null>(null);

export function useAuth(): AuthContextValue {
  const value = use(AuthContext);

  if (!value) {
    throw new Error("useAuth must be used inside <AuthProvider>.");
  }

  return value;
}

export function AuthProvider({
  locale,
  children,
}: {
  locale: string;
  children: React.ReactNode;
}) {
  const [status, setStatus] = useState<AuthStatus>("loading");
  const [user, setUser] = useState<User | null>(null);
  const [workspace, setWorkspace] = useState<string | null>(null);

  // The manager touches `window`, so it cannot be built during render on the server. Keyed by
  // workspace *and* authority so signing out of one tenant and into another cannot keep talking to
  // the previous realm.
  const managerRef = useRef<{ key: string; manager: UserManager }>(null);

  const managerFor = useCallback(
    (target: string, settings: OidcSettings) => {
      const key = `${settings.authority}|${target}`;

      if (managerRef.current?.key !== key) {
        managerRef.current = {
          key,
          manager: createUserManager(settings, target, window.location.origin, locale),
        };
      }

      return managerRef.current.manager;
    },
    [locale],
  );

  // Restore the session on mount, from what the last sign-in left behind. No network call: a rep
  // reopening the app offline has a valid session on the device and must not be shown a sign-in
  // screen because the config endpoint was unreachable.
  useEffect(() => {
    const remembered = readWorkspace();
    const settings = readSettings();

    let cancelled = false;

    // Every path resolves through the promise, including "nothing remembered". Setting state
    // synchronously in an effect is a cascading render on every mount; there is no reason for this
    // to be the exception.
    const restoring =
      remembered && settings ? managerFor(remembered, settings).getUser() : Promise.resolve(null);

    restoring
      .then((restored) => {
        if (cancelled) return;

        // `getUser` returns an expired user as readily as a live one — the expiry check is what
        // stops a stale token being presented as a session.
        const live = restored && !restored.expired ? restored : null;

        setWorkspace(remembered);
        setUser(live);
        setStatus(live ? "authenticated" : "anonymous");
      })
      .catch(() => {
        if (!cancelled) setStatus("anonymous");
      });

    return () => {
      cancelled = true;
    };
  }, [managerFor]);

  const expire = useCallback(() => {
    // Only from a live session. Without this guard a 401 arriving after sign-out — an in-flight
    // query settling a moment late — would drag the app back out of "anonymous" and show a re-auth
    // prompt to someone who just deliberately signed out.
    setStatus((current) => (current === "authenticated" ? "expired" : current));
  }, []);

  // The token dying while the app is open is the case the mount-time expiry check cannot see, and
  // the one a rep actually hits: the tab was open, `automaticSilentRenew` tried, and the refresh
  // token was gone or the network was not there.
  //
  // Gated on being online, because offline is the one situation where an expired token is not a
  // question the user can answer. Prompting a rep in a stockroom to re-authenticate offers them a
  // door that cannot open. If the session really is over, the first 401 after the network returns
  // says so — and that path costs nothing, since offline there is nothing to 401.
  useEffect(() => {
    const manager = managerRef.current?.manager;

    if (status !== "authenticated" || !manager) return;

    const onExpired = () => {
      if (navigator.onLine) expire();
    };

    manager.events.addAccessTokenExpired(onExpired);

    return () => manager.events.removeAccessTokenExpired(onExpired);
  }, [status, expire]);

  const signIn = useCallback(
    async (target: string, settings: OidcSettings) => {
      // Remembered *before* the redirect, not after: the browser leaves this origin and comes back
      // to a fresh page, and the callback has to know which realm minted the code it is holding.
      rememberWorkspace(target);
      rememberSettings(settings);
      setWorkspace(target);

      await managerFor(target, settings).signinRedirect();
    },
    [managerFor],
  );

  const completeSignIn = useCallback(
    async (settings: OidcSettings) => {
      const remembered = readWorkspace();

      if (!remembered) {
        throw new Error("No workspace to complete sign-in for.");
      }

      const signedIn = await managerFor(remembered, settings).signinCallback();

      rememberSettings(settings);
      setWorkspace(remembered);
      setUser(signedIn ?? null);
      setStatus(signedIn ? "authenticated" : "anonymous");
    },
    [managerFor],
  );

  const signOut = useCallback(async () => {
    const current = workspace ?? readWorkspace();
    const settings = readSettings();

    // Local state first, so a failed round trip to Keycloak still ends the session here rather than
    // leaving someone looking at a signed-in screen they cannot get out of.
    setUser(null);
    setStatus("anonymous");
    forgetWorkspace();
    forgetSettings();

    if (current && settings) {
      await managerFor(current, settings).signoutRedirect();
    }
  }, [workspace, managerFor]);

  const reauthenticate = useCallback(async () => {
    const target = workspace ?? readWorkspace();
    const settings = readSettings();

    // `readSettings` validates what it returns, so a tampered or half-written entry lands here as
    // null rather than as the authority of a redirect.
    if (!target || !settings) return false;

    await signIn(target, settings);
    return true;
  }, [workspace, signIn]);

  const value = useMemo<AuthContextValue>(
    () => ({ status, user, workspace, signIn, signOut, completeSignIn, expire, reauthenticate }),
    [status, user, workspace, signIn, signOut, completeSignIn, expire, reauthenticate],
  );

  return <AuthContext value={value}>{children}</AuthContext>;
}
