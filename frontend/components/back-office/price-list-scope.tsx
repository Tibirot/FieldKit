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
  assignmentsKey,
  fetchAssignments,
  fetchPriceLists,
  priceListsKey,
  setAssignments,
  type PriceList,
  type PriceListAssignment,
} from "@/lib/api/price-lists";
import { usePermissions } from "@/lib/auth/use-permissions";

/**
 * Which channels and outlets a price list reaches (`PRD-03`).
 *
 * **A list is priced before it is pointed anywhere.** Until this screen is used it is a draft: it
 * exists, it has prices, and it reaches nobody. Saving here is also what *announces* it — the server
 * raises `PriceListPublished` into the outbox in the same transaction, which Sync turns into a
 * reference delta.
 *
 * **Channels are the ordinary case; outlets are the exception** (`B1` assigns per channel with an
 * optional per-outlet override). That asymmetry is why the two halves of this screen look different:
 * a tenant has a handful of channels and can be shown all of them, and has thousands of outlets and
 * must search for the few that need special pricing.
 */
export function PriceListScope() {
  const t = useTranslations("PriceListScope");
  const { user } = useAuth();
  const params = useParams<{ id: string }>();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;
  const id = params.id;
  const enabled = Boolean(accessToken && subject && id);

  const lists = useQuery({
    enabled,
    queryKey: priceListsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchPriceLists(accessToken!, signal),
  });

  const channels = useQuery({
    enabled,
    queryKey: channelsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchChannels(accessToken!, signal),
  });

  const assignments = useQuery({
    enabled,
    queryKey: assignmentsKey(subject ?? "", id ?? ""),
    queryFn: ({ signal }) => fetchAssignments(accessToken!, id, signal),
  });

  const assignedOutletIds = useMemo(
    () =>
      (assignments.data ?? [])
        .map((assignment) => assignment.outletId)
        .filter((outletId): outletId is string => outletId !== null),
    [assignments.data],
  );

  const assigned = useAssignedOutlets(assignedOutletIds, t("unknownOutlet"), enabled);

  const failed = [lists, channels, assignments].find((query) => query.isError);

  if (failed) {
    const error = failed.error;

    // The assignments read 404s on a list this tenant does not have, which is also what another
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

  if (!lists.data || !channels.data || !assignments.data || assigned.pending) {
    return <p className="text-sm text-muted-foreground">{t("loading")}</p>;
  }

  const list = lists.data.find((candidate) => candidate.id === id);

  if (!list) {
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
        <h1 className="text-lg font-semibold tracking-tight">{list.name}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{t("intro", { currency: list.currency })}</p>
      </header>

      <ScopeEditor
        // Remounted per list, so the boxes reseed from what the server holds.
        key={list.id}
        list={list}
        channels={channels.data}
        assignments={assignments.data}
        assignedOutlets={assigned.outlets}
      />
    </div>
  );
}

/** The editable half, seeded once from what the server holds. */
function ScopeEditor({
  list,
  channels,
  assignments,
  assignedOutlets,
}: {
  list: PriceList;
  channels: readonly Channel[];
  assignments: readonly PriceListAssignment[];
  assignedOutlets: readonly OutletPick[];
}) {
  const t = useTranslations("PriceListScope");
  // Server refusals, in the reader's language (ADR-0012 stage 2).
  const refusals = useTranslations("Refusals");
  const client = useQueryClient();
  const { user } = useAuth();
  const { has } = usePermissions();

  const accessToken = user?.access_token;

  const storedChannels = useMemo(
    () =>
      new Set(
        assignments
          .map((assignment) => assignment.channelId)
          .filter((channelId): channelId is string => channelId !== null),
      ),
    [assignments],
  );

  const [chosenChannels, setChosenChannels] = useState<Set<string>>(() => new Set(storedChannels));
  const [chosenOutlets, setChosenOutlets] = useState<OutletPick[]>(() => [...assignedOutlets]);
  const [refused, setRefused] = useState<readonly string[]>([]);

  const save = useMutation({
    mutationFn: () =>
      setAssignments(accessToken!, list.id, {
        channelIds: [...chosenChannels],
        outletIds: chosenOutlets.map((outlet) => outlet.id),
      }),

    onSuccess: async () => {
      setRefused([]);
      await client.invalidateQueries({ queryKey: ["price-lists"] });
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
