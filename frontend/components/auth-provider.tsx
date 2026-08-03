"use client";

import type { User, UserManager } from "oidc-client-ts";
import { createContext, use, useCallback, useEffect, useMemo, useRef, useState } from "react";

import { createUserManager, type OidcSettings } from "@/lib/auth/oidc";
import { forgetSettings, readSettings, rememberSettings } from "@/lib/auth/settings-store";
import { forgetWorkspace, readWorkspace, rememberWorkspace } from "@/lib/auth/workspace";

/**
 * The signed-in session, for the whole app (`IAM-01`).
 *
 * `status` is three-valued rather than a boolean. "We have not looked yet" and "we looked and there
 * is nobody" are different things: collapsing them flashes the sign-in screen at an already
 * signed-in user on every reload, which on a phone reads as being logged out.
 */
export type AuthStatus = "loading" | "authenticated" | "anonymous";

export type AuthContextValue = {
  status: AuthStatus;
  user: User | null;
  workspace: string | null;
  /** Starts the redirect to a workspace's realm. Needs a live Keycloak address, so it is passed one. */
  signIn: (workspace: string, settings: OidcSettings) => Promise<void>;
  signOut: () => Promise<void>;
  /** Completes the redirect back from Keycloak. Only the callback route calls this. */
  completeSignIn: (settings: OidcSettings) => Promise<void>;
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

  const value = useMemo<AuthContextValue>(
    () => ({ status, user, workspace, signIn, signOut, completeSignIn }),
    [status, user, workspace, signIn, signOut, completeSignIn],
  );

  return <AuthContext value={value}>{children}</AuthContext>;
}
