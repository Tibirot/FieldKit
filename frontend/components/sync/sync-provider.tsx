"use client";

import { createContext, use, useCallback, useEffect, useMemo, useRef, useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { openDatabase, type FieldKitDatabase } from "@/lib/sync/db";
import { useLive } from "@/lib/sync/live";
import { startSync, type SyncManager, type SyncResult } from "@/lib/sync/manager";
import { pendingCount } from "@/lib/sync/outbox";

export type SyncContextValue = {
  /** The device's local store, for screens that read reference data or capture work. */
  db: FieldKitDatabase;
  /** How many mutations the server has not answered for. Live: it moves on capture *and* on drain. */
  pending: number;
  /** Whether a run is in flight, so the button can say so rather than looking ignored. */
  running: boolean;
  /** How the last run ended. `undefined` means it finished, `null` means none has run yet. */
  outcome: SyncResult["interrupted"] | null;
  /** Runs now, or joins the run already going (`OFF-06`). */
  syncNow: () => Promise<void>;
};

const SyncContext = createContext<SyncContextValue | null>(null);

export function useSync(): SyncContextValue {
  const value = use(SyncContext);

  if (!value) {
    throw new Error("useSync must be used inside <SyncProvider>.");
  }

  return value;
}

/**
 * Owns the device's store and its sync manager for one signed-in rep (`OFF-05`, `OFF-06`).
 *
 * <b>`tenant` and `subject` are props, not something this fetches.</b> The database name is
 * `fieldkit:<tenant>:<subject>` and opening the wrong one shows a rep somebody else's territory, so
 * the two identifiers arrive from whoever already knows them — the layout that called
 * `/api/auth/whoami` — rather than being resolved a second time here, where a loading state would
 * mean rendering children against no database or against the previous rep's.
 *
 * <b>The manager is started once per rep and stopped when that changes.</b> A manager left running
 * after a sign-out would sync one rep's outbox with another rep's device id, which is the
 * cross-contamination the per-user database exists to prevent, arriving through the back door.
 */
export function SyncProvider({
  tenant,
  subject,
  deviceId,
  children,
}: {
  tenant: string;
  subject: string;
  /**
   * The bound device (`OFF-12`). Passed in rather than bound here: binding is a write that
   * deactivates the rep's previous device, and a component that did it on mount would do it again
   * on every remount React felt like performing.
   */
  deviceId: string;
  children: React.ReactNode;
}) {
  const { user } = useAuth();
  const db = useMemo(() => openDatabase(tenant, subject), [tenant, subject]);

  const [running, setRunning] = useState(false);
  const [outcome, setOutcome] = useState<SyncResult["interrupted"] | null>(null);

  /*
   * The token is read through a ref rather than captured.
   *
   * A manager started at sign-in outlives several access tokens — `oidc-client-ts` renews them
   * silently — and a captured string would be stale on the first renewal, which is a 401 on every
   * sync from then on. The manager asks for the current one each run; this is what "current" means.
   *
   * Written in an effect, not during render. A ref assigned in the render body is a side effect in a
   * function React may call twice or throw away, and the compiler's rules say so — the effect runs
   * after every commit, which is the same "whenever it changed" with none of the ambiguity.
   */
  const token = useRef<string | null>(null);

  useEffect(() => {
    token.current = user?.access_token ?? null;
  }, [user]);

  const [manager, setManager] = useState<SyncManager | null>(null);

  useEffect(() => {
    const started = startSync(db, () => token.current, deviceId);
    setManager(started);

    return () => started.stop();
  }, [db, deviceId]);

  const pending = useLive(() => pendingCount(db), 0, [db]);

  const syncNow = useCallback(async () => {
    if (!manager) return;

    setRunning(true);

    try {
      const result = await manager.syncNow();
      setOutcome(result.interrupted);
    } catch {
      // A storage failure throws where a transport failure returns. Both leave the rep with work
      // still queued, and the indicator says so through `pending` — what this must not do is take
      // the screen down with it.
      setOutcome("failed");
    } finally {
      setRunning(false);
    }
  }, [manager]);

  const value = useMemo<SyncContextValue>(
    () => ({ db, pending, running, outcome, syncNow }),
    [db, pending, running, outcome, syncNow],
  );

  return <SyncContext value={value}>{children}</SyncContext>;
}
