"use client";

import { useMutation, useQueries, useQuery, useQueryClient } from "@tanstack/react-query";
import { X } from "lucide-react";
import { useTranslations } from "next-intl";
import { useParams } from "next/navigation";
import { useMemo, useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { Button } from "@/components/ui/button";
import { channelsKey, fetchChannels, type Channel } from "@/lib/api/channels";
import { ApiError } from "@/lib/api/client";
import { fetchOutlet, fetchOutlets, outletKey, outletsKey } from "@/lib/api/outlets";
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

const CONTROL =
  "h-9 w-full rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/** An outlet as the picker needs it — enough to recognise, no more. */
type Pick = { id: string; code: string; name: string };

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

  /**
   * One fetch per already-assigned outlet, to turn an id into something readable.
   *
   * There is no by-ids read on the outlet API, and this is the shape that does not pretend
   * otherwise. It is affordable because per-outlet assignment is an *override* by design — a handful
   * per list, not hundreds. If a tenant ever assigns enough outlets for this to be slow, that is the
   * signal the API needs a bulk read, not a reason to fetch the whole outlet base here and match
   * client-side.
   */
  const assigned = useQueries({
    queries: assignedOutletIds.map((outletId) => ({
      enabled,
      queryKey: outletKey(subject ?? "", outletId),
      queryFn: ({ signal }: { signal?: AbortSignal }) => fetchOutlet(accessToken!, outletId, signal),
    })),
  });

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

  if (!lists.data || !channels.data || !assignments.data || assigned.some((q) => q.isPending)) {
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

  // An outlet whose fetch failed is still assigned; dropping it here would silently remove it on the
  // next save. Shown by id so it is visible rather than lost.
  const outlets: Pick[] = assignedOutletIds.map((outletId, index) => {
    const loaded = assigned[index]?.data;

    return loaded
      ? { id: loaded.id, code: loaded.code, name: loaded.name }
      : { id: outletId, code: outletId, name: t("unknownOutlet") };
  });

  return (
    <div className="flex max-w-3xl flex-col gap-4">
      <header>
        <p className="font-mono text-[11.5px] text-muted-foreground">{t("crumb")}</p>
        <h1 className="text-lg font-semibold tracking-tight">{list.name}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{t("intro", { currency: list.currency })}</p>
      </header>

      <ScopeEditor
        // Remounted per list, so the boxes reseed from what the server holds.
        key={list.id}
        list={list}
        channels={channels.data}
        assignments={assignments.data}
        assignedOutlets={outlets}
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
  assignedOutlets: readonly Pick[];
}) {
  const t = useTranslations("PriceListScope");
  const client = useQueryClient();
  const { user } = useAuth();
  const { has } = usePermissions();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;

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
  const [chosenOutlets, setChosenOutlets] = useState<Pick[]>(() => [...assignedOutlets]);
  const [search, setSearch] = useState("");
  const [refused, setRefused] = useState<readonly string[]>([]);

  // Server-side, because the outlet base is paged and a client-side filter over one page would
  // search that page and look like it searched everything.
  const found = useQuery({
    enabled: Boolean(accessToken && subject) && search.trim().length >= 2,
    queryKey: outletsKey(subject ?? "", { search: search.trim(), pageSize: 10 }),
    queryFn: ({ signal }) =>
      fetchOutlets(accessToken!, { search: search.trim(), pageSize: 10 }, signal),
  });

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
          ? error.problems.map((problem) => problem.message)
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

        {chosenOutlets.length > 0 ? (
          <ul className="flex flex-wrap gap-2">
            {chosenOutlets.map((outlet) => (
              <li
                key={outlet.id}
                className="flex items-center gap-2 rounded-full border border-border px-3 py-1 text-xs"
              >
                <span className="font-mono text-muted-foreground">{outlet.code}</span>
                <span>{outlet.name}</span>

                {canWrite ? (
                  <button
                    type="button"
                    // The code, not just the name: a tenant may have several shops called "Mega
                    // Image Dorobanti", and three identically-named buttons is what a screen reader
                    // would then read out. The code is the tenant's own unique identifier.
                    aria-label={t("removeNamed", { name: outlet.name, code: outlet.code })}
                    onClick={() =>
                      setChosenOutlets((current) =>
                        current.filter((chosen) => chosen.id !== outlet.id),
                      )
                    }
                    className="text-muted-foreground hover:text-foreground"
                  >
                    <X className="size-3.5" />
                  </button>
                ) : null}
              </li>
            ))}
          </ul>
        ) : (
          <p className="text-sm text-muted-foreground">{t("noOutlets")}</p>
        )}

        {canWrite ? (
          <div className="flex flex-col gap-2">
            <input
              type="search"
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              placeholder={t("searchPlaceholder")}
              aria-label={t("searchOutlets")}
              className={`${CONTROL} max-w-sm`}
            />

            {search.trim().length >= 2 && found.data ? (
              found.data.items.length === 0 ? (
                <p className="text-sm text-muted-foreground">{t("noMatches", { search })}</p>
              ) : (
                <ul className="flex max-w-sm flex-col divide-y divide-border rounded-xl border border-border">
                  {found.data.items.map((outlet) => {
                    const already = chosenOutlets.some((chosen) => chosen.id === outlet.id);

                    return (
                      <li
                        key={outlet.id}
                        className="flex items-center gap-2 px-3 py-1.5 text-sm"
                      >
                        <span className="font-mono text-xs text-muted-foreground">{outlet.code}</span>
                        <span className="truncate">{outlet.name}</span>

                        <Button
                          type="button"
                          size="sm"
                          variant="outline"
                          className="ml-auto"
                          // Already chosen, so adding it again would silently do nothing. Disabled
                          // rather than hidden, so the result of a search stays a stable list.
                          disabled={already}
                          onClick={() =>
                            setChosenOutlets((current) => [
                              ...current,
                              { id: outlet.id, code: outlet.code, name: outlet.name },
                            ])
                          }
                          aria-label={t("addNamed", { name: outlet.name, code: outlet.code })}
                        >
                          {already ? t("added") : t("add")}
                        </Button>
                      </li>
                    );
                  })}
                </ul>
              )
            ) : null}
          </div>
        ) : null}
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
