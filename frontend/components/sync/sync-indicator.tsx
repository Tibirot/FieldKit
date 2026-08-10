"use client";

import { useTranslations } from "next-intl";

import { useSync } from "@/components/sync/sync-provider";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { useConnectivity } from "@/lib/sync/connectivity";

/**
 * What a rep needs to know about their unsent work, and one button (`OFF-05`, `OFF-06`).
 *
 * <b>The pending count is the fact; connectivity is the explanation.</b> A green "online" tick would
 * be answering the wrong question — `navigator.onLine` is true on a captive portal, and a rep who
 * reads it as "my visits are in" has been misled by the app. So the headline is always *how much
 * work has not reached the back office*, and being offline only explains why.
 *
 * <b>Nothing is hidden when everything is fine.</b> The synced state renders too, quietly, because
 * an indicator that disappears cannot be distinguished from an indicator that is broken — and a rep
 * standing outside a shop deciding whether they can close the app needs to see an answer, not the
 * absence of a warning.
 */
export function SyncIndicator() {
  const t = useTranslations("Sync");
  const online = useConnectivity();
  const { pending, running, outcome, syncNow } = useSync();

  const state = describe({ online, pending, running, outcome });

  return (
    <div className="flex items-center gap-2" data-testid="sync-indicator">
      <Badge variant={state.variant} aria-live="polite">
        {t(state.message, { count: pending })}
      </Badge>

      {/*
        Offered even when offline, and that is deliberate. `navigator.onLine` is a guess, so a rep
        who believes they have signal must be able to try — a disabled button would make the app's
        wrong guess final. What it must not do is start a second run over the top of a live one.
      */}
      <Button size="sm" variant="outline" onClick={() => void syncNow()} disabled={running}>
        {running ? t("syncing") : t("syncNow")}
      </Button>
    </div>
  );
}

/**
 * `message` is a union rather than `string` so the type checker holds it to the catalogue.
 *
 * `next-intl` types `t()` against the message keys, and a widened `string` throws that away — which
 * is how a chip ends up rendering `Sync.pendign` at a rep instead of failing to build.
 */
type State = {
  message: "rebind" | "signInAgain" | "syncing" | "offline" | "pending" | "synced";
  variant: "default" | "secondary" | "destructive" | "outline";
};

/**
 * The one place the states are ranked, so the chip cannot say two things at once.
 *
 * Order matters and is the whole design. A rejection outranks everything, because it is the only
 * state that needs a person rather than a connection. Offline outranks a pending count, because it
 * tells the rep the count is not their fault. "Synced" is last, and only reachable when there is
 * genuinely nothing outstanding.
 */
function describe({
  online,
  pending,
  running,
  outcome,
}: {
  online: boolean;
  pending: number;
  running: boolean;
  outcome: string | null | undefined;
}): State {
  if (outcome === "deviceRejected") return { message: "rebind", variant: "destructive" };
  if (outcome === "unauthorized") return { message: "signInAgain", variant: "destructive" };
  if (running) return { message: "syncing", variant: "secondary" };
  if (!online) return { message: "offline", variant: "outline" };
  if (pending > 0) return { message: "pending", variant: "secondary" };

  return { message: "synced", variant: "outline" };
}
