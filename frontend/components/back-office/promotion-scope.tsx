"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useParams } from "next/navigation";
import { useMemo, useState } from "react";

import { useAuth } from "@/components/auth-provider";
import {
  OutletPicker,
  useAssignedOutlets,
  type OutletPick,
} from "@/components/back-office/outlet-picker";
import { Breadcrumb } from "@/components/back-office/breadcrumb";
import { Button } from "@/components/ui/button";
import { channelsKey, fetchChannels, type Channel } from "@/lib/api/channels";
import { ApiError } from "@/lib/api/client";
import { refusalTexts } from "@/lib/api/refusals";
import {
  fetchPromotions,
  fetchPromotionScope,
  promotionScopeKey,
  promotionsKey,
  setPromotionScope,
  type Promotion,
  type PromotionAssignment,
} from "@/lib/api/promotions";
import { usePermissions } from "@/lib/auth/use-permissions";

/**
 * Which channels and outlets a promotion reaches (`PRD-05`).
 *
 * **The last thing a promotion needs before it does anything.** Type, value, targets and window all
 * describe a rule; this says who it happens to. Saving is also what *announces* it — the server
 * raises `PromotionActivated` into the outbox in the same transaction, which Sync turns into a
 * reference delta.
 *
 * **Scope is not the same question as targets.** Targets say *what* is discounted (products and
 * categories); this says *where* the discount runs. A deal on Veridian Still that reaches only
 * Modern Trade needs both, and either one left empty means it never fires.
 */
export function PromotionScope() {
  const t = useTranslations("PromotionScope");
  const { user } = useAuth();
  const params = useParams<{ id: string }>();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;
  const id = params.id;
  const enabled = Boolean(accessToken && subject && id);

  const promotions = useQuery({
    enabled,
    queryKey: promotionsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchPromotions(accessToken!, signal),
  });

  const channels = useQuery({
    enabled,
    queryKey: channelsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchChannels(accessToken!, signal),
  });

  const scope = useQuery({
    enabled,
    queryKey: promotionScopeKey(subject ?? "", id ?? ""),
    queryFn: ({ signal }) => fetchPromotionScope(accessToken!, id, signal),
  });

  const assignedOutletIds = useMemo(
    () =>
      (scope.data ?? [])
        .map((assignment) => assignment.outletId)
        .filter((outletId): outletId is string => outletId !== null),
    [scope.data],
  );

  const assigned = useAssignedOutlets(assignedOutletIds, t("unknownOutlet"), enabled);

  const failed = [promotions, channels, scope].find((query) => query.isError);

  if (failed) {
    const error = failed.error;

    // The scope read 404s on a promotion this tenant does not have, which is also what another
    // tenant's id looks like from here.
    if (error instanceof ApiError && error.status === 404) {
      return (
        <p role="alert" className="text-sm text-destructive">
          {t("notFound")}
        </p>
      );
    }

    return (
      <p role="alert" className="text-sm text-destructive">
        {error instanceof ApiError && error.status === 403 ? t("forbidden") : t("failed")}
      </p>
    );
  }

  if (!promotions.data || !channels.data || !scope.data || assigned.pending) {
    return <p className="text-sm text-muted-foreground">{t("loading")}</p>;
  }

  const promotion = promotions.data.find((candidate) => candidate.id === id);

  if (!promotion) {
    return (
      <p role="alert" className="text-sm text-destructive">
        {t("notFound")}
      </p>
    );
  }

  return (
    <div className="flex max-w-3xl flex-col gap-4">
      <header>
        <Breadcrumb leaf={t("crumbLeaf")} />
        <h1 className="text-lg font-semibold tracking-tight">{promotion.name}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
      </header>

      <ScopeEditor
        // Remounted per promotion, so the boxes reseed from what the server holds.
        key={promotion.id}
        promotion={promotion}
        channels={channels.data}
        scope={scope.data}
        assignedOutlets={assigned.outlets}
      />
    </div>
  );
}

/** The editable half, seeded once from what the server holds. */
function ScopeEditor({
  promotion,
  channels,
  scope,
  assignedOutlets,
}: {
  promotion: Promotion;
  channels: readonly Channel[];
  scope: readonly PromotionAssignment[];
  assignedOutlets: readonly OutletPick[];
}) {
  const t = useTranslations("PromotionScope");
  // Server refusals, in the reader's language (ADR-0012 stage 2).
  const refusals = useTranslations("Refusals");
  const client = useQueryClient();
  const { user } = useAuth();
  const { has } = usePermissions();

  const accessToken = user?.access_token;

  const storedChannels = useMemo(
    () =>
      new Set(
        scope
          .map((assignment) => assignment.channelId)
          .filter((channelId): channelId is string => channelId !== null),
      ),
    [scope],
  );

  const [chosenChannels, setChosenChannels] = useState<Set<string>>(() => new Set(storedChannels));
  const [chosenOutlets, setChosenOutlets] = useState<OutletPick[]>(() => [...assignedOutlets]);
  const [refused, setRefused] = useState<readonly string[]>([]);

  const save = useMutation({
    mutationFn: () =>
      setPromotionScope(accessToken!, promotion.id, {
        channelIds: [...chosenChannels],
        outletIds: chosenOutlets.map((outlet) => outlet.id),
      }),

    onSuccess: async () => {
      setRefused([]);
      await client.invalidateQueries({ queryKey: ["promotions"] });
    },

    onError: (error) =>
      setRefused(
        error instanceof ApiError && error.problems.length > 0
          ? refusalTexts(refusals, error.problems)
          : [t("saveFailed")],
      ),
  });

  const dirty = useMemo(() => {
    if (chosenChannels.size !== storedChannels.size) return true;
    if (chosenOutlets.length !== assignedOutlets.length) return true;

    for (const channelId of chosenChannels) if (!storedChannels.has(channelId)) return true;

    const before = new Set(assignedOutlets.map((outlet) => outlet.id));

    return chosenOutlets.some((outlet) => !before.has(outlet.id));
  }, [chosenChannels, storedChannels, chosenOutlets, assignedOutlets]);

  const canWrite = has("product:write");
  const reaches = chosenChannels.size + chosenOutlets.length;

  function toggleChannel(channelId: string, on: boolean) {
    setChosenChannels((current) => {
      const next = new Set(current);
      if (on) next.add(channelId);
      else next.delete(channelId);
      return next;
    });
  }

  return (
    <div className="flex flex-col gap-6">
      <p className="text-sm text-muted-foreground" role="status">
        {reaches === 0 ? t("reachesNobody") : t("reaches", { count: reaches })}
      </p>

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-xl bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      <section className="flex flex-col gap-2">
        <h2 className="text-sm font-semibold">{t("channels")}</h2>
        <p className="text-xs text-muted-foreground">{t("channelsHint")}</p>

        {channels.length === 0 ? (
          <p className="text-sm text-muted-foreground">{t("noChannels")}</p>
        ) : (
          <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
            {channels.map((channel) => (
              <li key={channel.id} className="px-4 py-2 text-sm">
                <label className="flex items-center gap-2">
                  <input
                    type="checkbox"
                    className="size-4"
                    disabled={!canWrite}
                    checked={chosenChannels.has(channel.id)}
                    onChange={(event) => toggleChannel(channel.id, event.target.checked)}
                    aria-label={t("channelNamed", { name: channel.name })}
                  />
                  {channel.name}
                </label>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="flex flex-col gap-2">
        <h2 className="text-sm font-semibold">{t("outlets")}</h2>
        <p className="text-xs text-muted-foreground">{t("outletsHint")}</p>

        {chosenOutlets.length === 0 ? (
          <p className="text-sm text-muted-foreground">{t("noOutlets")}</p>
        ) : null}

        <OutletPicker
          chosen={chosenOutlets}
          onChange={setChosenOutlets}
          canWrite={canWrite}
          labels={{
            search: t("searchOutlets"),
            searchPlaceholder: t("searchPlaceholder"),
            noMatches: (search) => t("noMatches", { search }),
            add: t("add"),
            added: t("added"),
            addNamed: (outlet) => t("addNamed", { name: outlet.name, code: outlet.code }),
            removeNamed: (outlet) => t("removeNamed", { name: outlet.name, code: outlet.code }),
          }}
        />
      </section>

      {canWrite ? (
        <div className="flex items-center gap-3">
          <Button
            type="button"
            size="sm"
            disabled={!dirty || save.isPending}
            onClick={() => save.mutate()}
          >
            {save.isPending ? t("saving") : t("save")}
          </Button>

          {dirty ? <span className="text-xs text-muted-foreground">{t("unsaved")}</span> : null}
        </div>
      ) : null}
    </div>
  );
}
