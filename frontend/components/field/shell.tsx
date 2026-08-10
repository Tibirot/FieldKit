"use client";

import { useTranslations } from "next-intl";
import { useEffect, useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { SessionGuard } from "@/components/session-guard";
import { SyncIndicator } from "@/components/sync/sync-indicator";
import { SyncProvider, useSync } from "@/components/sync/sync-provider";
import { Button } from "@/components/ui/button";
import { openDatabase } from "@/lib/sync/db";
import { ensureDevice } from "@/lib/sync/manager";

/**
 * The field app's frame (`OFF-05`, `OFF-06`, `OFF-12`) — W9 slice 1.
 *
 * <b>This is the mount point W8 shipped without.</b> `SyncProvider`, `SyncIndicator` and
 * `SyncBadge` were built, tested, and rendered by nothing; so were the local-store readers every
 * screen below this will use. Until a layout owns a database and a bound device, none of them can
 * be reached, which is why this slice comes before any screen.
 *
 * <b>Mobile-first, and a different shape from the back office.</b> Two experiences, one app
 * (ADR-0004): no sidebar, one line of chrome, and the sync state in it — because the question a rep
 * asks standing outside a shop is "has my work gone in", and it should never be more than a glance
 * away.
 */
export function FieldShell({ children }: { children: React.ReactNode }) {
  return (
    <SessionGuard>
      <BoundDevice>{children}</BoundDevice>
    </SessionGuard>
  );
}

/** What the shell is doing about the one thing it needs before anything else can run. */
type Binding =
  /** Looking in the local store, which is the offline-safe answer and almost always the one. */
  | { state: "checking" }
  /** Bound. Everything below can sync. */
  | { state: "bound"; deviceId: string }
  /**
   * Never bound on this browser, and the bind needs a server.
   *
   * The one state in the field app that genuinely cannot be reached offline: a device id is minted
   * by `POST /api/sync/devices`, and until there is one there is nothing to pull *into* either.
   */
  | { state: "unbindable" };

/**
 * Resolves the rep's database and device, then hands both to `SyncProvider`.
 *
 * <b>`workspace`, not the tenant id from `/api/auth/whoami`.</b> The database name has to be stable
 * and available with no network — it decides which rep's territory this browser opens — and the
 * workspace slug is on the device from sign-in while the tenant guid is one API call away. A shell
 * that had to ask the server who you are before opening your local store would be an offline-first
 * app with a network dependency in front of its offline data.
 */
function BoundDevice({ children }: { children: React.ReactNode }) {
  const t = useTranslations("Field");
  const { user, workspace, signOut } = useAuth();

  const subject = user?.profile.sub;
  const accessToken = user?.access_token;

  const [binding, setBinding] = useState<Binding>({ state: "checking" });

  const db = workspace && subject ? openDatabase(workspace, subject) : null;

  /*
   * The bind runs *inside* the effect rather than in a callback the effect calls.
   *
   * `react-hooks/set-state-in-effect` refuses the latter, and it is right to: a `setState` reachable
   * synchronously from an effect re-renders before the effect has finished. Doing the work in an
   * async body puts every `setBinding` after an `await`, which is also where the cancellation guard
   * belongs — a rep who navigates away mid-bind should not have the answer land on a dead component.
   *
   * `attempt` is how the retry button re-runs it: bumping a counter is a *dependency* change, which
   * is the effect's own vocabulary, rather than a second path into the same work.
   */
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (!db || !accessToken) return;

    let cancelled = false;

    void (async () => {
      try {
        const deviceId = await ensureDevice(db, accessToken);
        if (!cancelled) setBinding({ state: "bound", deviceId });
      } catch {
        // `ensureDevice` answers from the local store without touching the network, so reaching
        // here means there was no stored id *and* the bind failed. Offline, or a server that
        // refused: the rep is in the same place either way, and the answer is the same.
        if (!cancelled) setBinding({ state: "unbindable" });
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [db, accessToken, attempt]);

  const retry = () => {
    setBinding({ state: "checking" });
    setAttempt((previous) => previous + 1);
  };

  /** After a rebind the id in `meta` is gone, so this re-runs the same first-bind path. */
  const rebind = async () => {
    setBinding({ state: "checking" });
    setAttempt((previous) => previous + 1);
  };

  if (!db || !workspace || !subject) {
    // `SessionGuard` has already established a session, so this is the gap between that and the
    // token's claims being readable rather than a state a rep can sit in.
    return <Waiting message={t("opening")} />;
  }

  if (binding.state === "checking") return <Waiting message={t("binding")} />;

  if (binding.state === "unbindable") {
    return (
      <Explained title={t("unbindable.title")} body={t("unbindable.body")}>
        <Button onClick={retry}>{t("unbindable.retry")}</Button>
        <Button variant="outline" onClick={() => void signOut()}>
          {t("signOut")}
        </Button>
      </Explained>
    );
  }

  return (
    <SyncProvider tenant={workspace} subject={subject} deviceId={binding.deviceId}>
      <FieldFrame onRebind={rebind}>{children}</FieldFrame>
    </SyncProvider>
  );
}

/**
 * Everything the shell does that needs a live sync context (`OFF-06`, `OFF-12`).
 *
 * <b>It syncs when the app opens.</b> `startSync` runs on `online` and on request, which covers a
 * rep who loses signal and gets it back — and not the ordinary morning, where the app is opened on
 * a working connection and no event ever fires. Without this the first sync of the day waits for
 * the rep to press a button to fetch the journey they opened the app to read.
 *
 * <b>It stands aside entirely while the device is rejected</b>, rather than showing a banner. A
 * rejected device cannot pull, so everything below is frozen at the last sync, and a rep working a
 * stale journey is the failure this state exists to prevent.
 */
function FieldFrame({
  onRebind,
  children,
}: {
  onRebind: () => Promise<void>;
  children: React.ReactNode;
}) {
  const t = useTranslations("Field");
  const { db, pending, outcome, syncNow } = useSync();
  const [rebinding, setRebinding] = useState(false);

  useEffect(() => {
    // Once per mounted provider. `syncNow` is single-flight, so this joins a run already going
    // rather than racing one, and a failure is not thrown at the screen — it lands in `outcome`,
    // which is what the indicator in the header is for.
    void syncNow();
  }, [syncNow]);

  if (outcome !== "deviceRejected") {
    return (
      <div className="flex min-h-dvh flex-col">
        <header className="flex items-center justify-between gap-3 border-b border-border px-4 py-2">
          <span className="text-sm font-medium">{t("title")}</span>
          <SyncIndicator />
        </header>
        <main className="min-w-0 flex-1 p-4">{children}</main>
      </div>
    );
  }

  const rebind = async () => {
    setRebinding(true);

    // Only the id. The watermarks stay, and that is not an oversight: the server keys a device's
    // recorded scope by device id, so a *new* id has no scope, every outlet reads as entering, and
    // the baselines arrive whatever the cursor says (`PullEndpoints.TerritoryAsync`). Clearing them
    // here would re-download the catalogue and the price lines to solve a problem the server has
    // already solved.
    await db.meta.delete("deviceId");
    await onRebind();

    setRebinding(false);
  };

  return (
    <Explained title={t("rebind.title")} body={t("rebind.body", { count: pending })}>
      <Button onClick={() => void rebind()} disabled={rebinding}>
        {rebinding ? t("rebind.working") : t("rebind.action")}
      </Button>
    </Explained>
  );
}

function Waiting({ message }: { message: string }) {
  return (
    <main className="grid min-h-dvh place-items-center p-6">
      <p className="text-sm text-muted-foreground" role="status">
        {message}
      </p>
    </main>
  );
}

function Explained({
  title,
  body,
  children,
}: {
  title: string;
  body: string;
  children: React.ReactNode;
}) {
  return (
    <main className="grid min-h-dvh place-items-center p-6">
      <div className="flex max-w-sm flex-col items-center gap-4 text-center" role="alert">
        <div className="flex flex-col gap-1">
          <h1 className="text-lg font-medium">{title}</h1>
          <p className="text-sm text-muted-foreground">{body}</p>
        </div>
        <div className="flex flex-wrap items-center justify-center gap-2">{children}</div>
      </div>
    </main>
  );
}
