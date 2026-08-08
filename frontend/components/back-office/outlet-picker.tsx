"use client";

import { useQueries, useQuery } from "@tanstack/react-query";
import { X } from "lucide-react";
import { useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { Button } from "@/components/ui/button";
import { fetchOutlet, fetchOutlets, outletKey, outletsKey } from "@/lib/api/outlets";

const CONTROL =
  "h-9 w-full rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/** An outlet as a picker needs it — enough to recognise, no more. */
export type OutletPick = { id: string; code: string; name: string };

/** The fewest characters worth asking the server about. */
export const SHORTEST_SEARCH = 2;

/**
 * Every prop this component would otherwise have to name itself.
 *
 * Passed in rather than read from one namespace, because the two screens using this say different
 * things about the same control: a price list's outlets are shops that pay something other than
 * their channel, a promotion's are shops a deal reaches beyond its channels. Sharing the mechanism
 * should not flatten the wording.
 */
export type OutletPickerLabels = {
  search: string;
  searchPlaceholder: string;
  noMatches: (search: string) => string;
  add: string;
  added: string;
  addNamed: (outlet: OutletPick) => string;
  removeNamed: (outlet: OutletPick) => string;
};

/**
 * Turns assigned outlet ids into something readable.
 *
 * **One fetch per id.** There is no by-ids read on the outlet API, and this is the shape that does
 * not pretend otherwise. It is affordable because per-outlet assignment is an *override* by design —
 * a handful per list or promotion, not hundreds. If a tenant ever names enough outlets for this to
 * drag, that is the signal the API needs a bulk read, not a reason to fetch the whole outlet base
 * here and match client-side.
 *
 * **An outlet whose fetch fails is still returned**, carrying its id where its code would be. The
 * assignment exists server-side regardless of whether this screen could read it, and the PUT that
 * follows replaces the whole set — so dropping it here would silently unassign a shop because a GET
 * went wrong.
 */
export function useAssignedOutlets(
  outletIds: readonly string[],
  unknownName: string,
  enabled: boolean,
): { outlets: OutletPick[]; pending: boolean } {
  const { user } = useAuth();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;

  const assigned = useQueries({
    queries: outletIds.map((outletId) => ({
      enabled,
      queryKey: outletKey(subject ?? "", outletId),
      queryFn: ({ signal }: { signal?: AbortSignal }) => fetchOutlet(accessToken!, outletId, signal),
    })),
  });

  return {
    pending: assigned.some((query) => query.isPending),
    outlets: outletIds.map((outletId, index) => {
      const loaded = assigned[index]?.data;

      return loaded
        ? { id: loaded.id, code: loaded.code, name: loaded.name }
        : { id: outletId, code: outletId, name: unknownName };
    }),
  };
}

/**
 * Chosen outlets as chips, and a search box for adding more.
 *
 * **The search goes to the server.** The outlet base is paged, and a client-side filter over one
 * page would search that page while looking like it searched everything a tenant has. (The product
 * picker on a promotion's targets reaches the opposite conclusion from the same argument, because
 * that endpoint returns the whole catalogue at once.)
 *
 * Shared by the two screens that say where something applies. What differs between them is the
 * wording, which arrives as `labels`, and what an empty set *means* — a withdrawn price list, a
 * promotion that reaches nobody — which each screen says for itself.
 */
export function OutletPicker({
  chosen,
  onChange,
  canWrite,
  labels,
}: {
  chosen: readonly OutletPick[];
  onChange: (next: OutletPick[]) => void;
  canWrite: boolean;
  labels: OutletPickerLabels;
}) {
  const { user } = useAuth();
  const [search, setSearch] = useState("");

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;
  const needle = search.trim();

  const found = useQuery({
    enabled: Boolean(accessToken && subject) && needle.length >= SHORTEST_SEARCH,
    queryKey: outletsKey(subject ?? "", { search: needle, pageSize: 10 }),
    queryFn: ({ signal }) => fetchOutlets(accessToken!, { search: needle, pageSize: 10 }, signal),
  });

  return (
    <>
      {chosen.length > 0 ? (
        <ul className="flex flex-wrap gap-2">
          {chosen.map((outlet) => (
            <li
              key={outlet.id}
              className="flex items-center gap-2 rounded-full border border-border px-3 py-1 text-xs"
            >
              <span className="font-mono text-muted-foreground">{outlet.code}</span>
              <span>{outlet.name}</span>

              {canWrite ? (
                <button
                  type="button"
                  // The code as well as the name: a tenant may have several shops called "Mega
                  // Image Dorobanti", and identically-named controls is what a screen reader would
                  // then read out.
                  aria-label={labels.removeNamed(outlet)}
                  onClick={() => onChange(chosen.filter((candidate) => candidate.id !== outlet.id))}
                  className="text-muted-foreground hover:text-foreground"
                >
                  <X className="size-3.5" />
                </button>
              ) : null}
            </li>
          ))}
        </ul>
      ) : null}

      {canWrite ? (
        <div className="flex flex-col gap-2">
          <input
            type="search"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
            placeholder={labels.searchPlaceholder}
            aria-label={labels.search}
            className={`${CONTROL} max-w-sm`}
          />

          {needle.length >= SHORTEST_SEARCH && found.data ? (
            found.data.items.length === 0 ? (
              <p className="text-sm text-muted-foreground">{labels.noMatches(search)}</p>
            ) : (
              <ul className="flex max-w-sm flex-col divide-y divide-border rounded-xl border border-border">
                {found.data.items.map((outlet) => {
                  const already = chosen.some((candidate) => candidate.id === outlet.id);

                  return (
                    <li key={outlet.id} className="flex items-center gap-2 px-3 py-1.5 text-sm">
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
                          onChange([
                            ...chosen,
                            { id: outlet.id, code: outlet.code, name: outlet.name },
                          ])
                        }
                        aria-label={labels.addNamed({
                          id: outlet.id,
                          code: outlet.code,
                          name: outlet.name,
                        })}
                      >
                        {already ? labels.added : labels.add}
                      </Button>
                    </li>
                  );
                })}
              </ul>
            )
          ) : null}
        </div>
      ) : null}
    </>
  );
}
