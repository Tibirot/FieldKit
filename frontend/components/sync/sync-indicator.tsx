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
  const { pending, failed, running, outcome, syncNow } = useSync();

  const state = describe({ online, pending, failed, running, outcome });

  return (
    <div className="flex items-center gap-2" data-testid="sync-indicator">
      <Badge variant={state.variant} aria-live="polite">
        {t(state.message, { count: state.message === "needsAttention" ? failed : pending })}
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
  message: "rebind" | "signInAgain" | "needsAttention" | "syncing" | "offline" | "pending" | "synced";
  variant: "default" | "secondary" | "destructive" | "outline";
};

/**
 * The one place the states are ranked, so the chip cannot say two things at once.
 *
 * Order matters and is the whole design. A rejection outranks everything, because it is the only
 * state that needs a person rather than a connection. Offline outranks a pending count, because it
 * tells the rep the count is not their fault. "Synced" is last, and only reachable when there is
 * genuinely nothing outstanding.
 *
 * <b>That first sentence was true of the intent and false of the code until W11 slice 8c.</b>
 * `outcome` is how the last *run* ended, not whether any work was refused — so a mutation the server
 * rejected ranked nowhere at all, and the chip fell through to "Everything synced" over an order the
 * rep had lost. `failed` is the count that was missing.
 */
function describe({
  online,
  pending,
  failed,
  running,
  outcome,
}: {
  online: boolean;
  pending: number;
  failed: number;
  running: boolean;
  outcome: string | null | undefined;
}): State {
  if (outcome === "deviceRejected") return { message: "rebind", variant: "destructive" };
  if (outcome === "unauthorized") return { message: "signInAgain", variant: "destructive" };
  /*
   * Refused work outranks everything a connection could fix, and this line is the bug W11 slice 8c
   * was opened for: only `pending` was counted, so an order the server refused was invisible and the
   * chip read **"Everything synced"** over work that was gone. A rep was told their day was safe.
   *
   * Above `syncing` and `offline` on purpose. Both of those are temporary and neither is the rep's
   * problem; this one does not clear on its own and needs a person (`OFF-09`).
   */
  if (failed > 0) return { message: "needsAttention", variant: "destructive" };

  if (running) return { message: "syncing", variant: "secondary" };
  if (!online) return { message: "offline", variant: "outline" };
  if (pending > 0) return { message: "pending", variant: "secondary" };

  return { message: "synced", variant: "outline" };
}
