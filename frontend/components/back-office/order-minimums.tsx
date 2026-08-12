"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useMemo, useState } from "react";

import { useAuth } from "@/components/auth-provider";
import {
  OutletPicker,
  useAssignedOutlets,
  type OutletPick,
} from "@/components/back-office/outlet-picker";
import { Button } from "@/components/ui/button";
import { channelsKey, fetchChannels, type Channel } from "@/lib/api/channels";
import { ApiError } from "@/lib/api/client";
import {
  fetchOrderMinimums,
  orderMinimumsKey,
  setOrderMinimums,
  type OrderMinimum,
  type OrderMinimumWrite,
} from "@/lib/api/order-minimums";
import { fetchPriceLists, looksLikeAnAmount, priceListsKey } from "@/lib/api/price-lists";
import { refusalTexts } from "@/lib/api/refusals";
import { usePermissions } from "@/lib/auth/use-permissions";

/**
 * The smallest order a shop may place (`ORD-06`, `BR-ORD-5`) — W11 slice 8b-iii.
 *
 * <b>The last of the three: 8b-i gave the server a minimum, 8b-ii carried it to the device and made
 * it refuse, and this is where somebody sets one.</b> Until now the only way to author a minimum was
 * a `PUT` by hand, which meant the rule a rep meets at a counter came from nowhere they could see.
 *
 * <b>Channels are shown; outlets are searched for</b> — the same asymmetry `PriceListScope` explains,
 * and for the same reason. `B1` sets a minimum per channel with a per-outlet override, so a tenant
 * has a handful of channels and can be shown all of them at once, and thousands of outlets among
 * which only a few are exceptions.
 *
 * <b>Blank is how a minimum is removed</b>, rather than a delete button. The `PUT` replaces the whole
 * set, so an amount cleared here simply is not sent — which makes "no minimum" and "a minimum of
 * nothing" the same gesture, and the server refuses zero precisely so they cannot become two
 * different states.
 */
export function OrderMinimums() {
  const t = useTranslations("OrderMinimums");
  const { user } = useAuth();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;
  const enabled = Boolean(accessToken && subject);

  const channels = useQuery({
    enabled,
    queryKey: channelsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchChannels(accessToken!, signal),
  });

  const minimums = useQuery({
    enabled,
    queryKey: orderMinimumsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchOrderMinimums(accessToken!, signal),
  });

  /*
   * The price lists are read for one field: what currency to suggest.
   *
   * `BR-ORD-7` takes an order's currency from the list that priced it, and nothing makes that agree
   * with what somebody types here — a mismatch is a refusal the *rep* meets, at a counter, about a
   * misconfiguration they cannot fix. Suggesting the currency the tenant actually prices in is the
   * cheapest way to stop that happening at all.
   */
  const priceLists = useQuery({
    enabled,
    queryKey: priceListsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchPriceLists(accessToken!, signal),
  });

  const storedOutletIds = useMemo(
    () =>
      (minimums.data ?? [])
        .map((minimum) => minimum.outletId)
        .filter((outletId): outletId is string => outletId !== null),
    [minimums.data],
  );

  const stored = useAssignedOutlets(storedOutletIds, t("unknownOutlet"), enabled);

  const failed = [channels, minimums, priceLists].find((query) => query.isError);

  if (failed) {
    const error = failed.error;

    return (
      <p role="alert" className="text-sm text-destructive">
        {error instanceof ApiError && error.status === 403 ? t("forbidden") : t("failed")}
      </p>
    );
  }

  if (!channels.data || !minimums.data || !priceLists.data || stored.pending) {
    return <p className="text-sm text-muted-foreground">{t("loading")}</p>;
  }

  /*
   * One currency across every price list, or none suggested.
   *
   * Most tenants price in one currency, and for them this is simply right. A tenant pricing in
   * several has no single answer — the currency that matters is the one on the list reaching *this*
   * channel, which needs the assignments of every list — so it asks rather than guessing, because a
   * wrong suggestion here is worse than an empty box: it saves without complaint and surfaces as a
   * rep being turned away.
   */
  const currencies = new Set(priceLists.data.map((list) => list.currency));
  const suggested = currencies.size === 1 ? [...currencies][0] : "";

  return (
    <div className="flex max-w-3xl flex-col gap-4">
      <header>
        <p className="font-mono text-[11.5px] text-muted-foreground">{t("crumb")}</p>
        <h1 className="text-lg font-semibold tracking-tight">{t("title")}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{t("intro")}</p>
      </header>

      {/*
        <b>No `key`, deliberately — it used to be `minimums.dataUpdatedAt` and that was a bug.</b>

        The intent was to reseed the boxes after a save. What it actually did was remount on *every*
        refetch, and React Query refetches on window focus: an author who alt-tabbed mid-edit came
        back to an empty screen, with every amount typed and every outlet added silently discarded.
        Found in a browser; no unit test could see it, because none of them refetch.

        Mounting once is also the simpler behaviour. `seeded` still arrives as a prop, so the
        dirty comparison tracks the server's answer — after a save it matches what the author has on
        screen and the button settles by itself, and if another author changes the set underneath,
        this one keeps their edits and is told they are unsaved. Silently replacing somebody's
        in-progress work with somebody else's is the outcome the `key` actually bought.
      */}
      <Editor
        channels={channels.data}
        minimums={minimums.data}
        storedOutlets={stored.outlets}
        suggestedCurrency={suggested}
      />
    </div>
  );
}

/** One row being edited. `amount` blank means "no minimum here". */
type Row = { amount: string; currencyCode: string };

const NOTHING: Row = { amount: "", currencyCode: "" };

/** The editable half, seeded once from what the server holds. */
function Editor({
  channels,
  minimums,
  storedOutlets,
  suggestedCurrency,
}: {
  channels: readonly Channel[];
  minimums: readonly OrderMinimum[];
  storedOutlets: readonly OutletPick[];
  suggestedCurrency: string;
}) {
  const t = useTranslations("OrderMinimums");
  // Server refusals, in the reader's language (ADR-0012 stage 2).
  const refusals = useTranslations("Refusals");
  const client = useQueryClient();
  const { user } = useAuth();
  const { has } = usePermissions();

  const accessToken = user?.access_token;
  const canWrite = has("product:write");

  const seeded = useMemo(() => {
    const rows = new Map<string, Row>();

    for (const minimum of minimums) {
      const scopeId = minimum.channelId ?? minimum.outletId;
      if (scopeId) rows.set(scopeId, { amount: minimum.amount, currencyCode: minimum.currencyCode });
    }

    return rows;
  }, [minimums]);

  const [rows, setRows] = useState<Map<string, Row>>(() => new Map(seeded));
  const [outlets, setOutlets] = useState<OutletPick[]>(() => [...storedOutlets]);
  const [refused, setRefused] = useState<readonly string[]>([]);

  const row = (scopeId: string) => rows.get(scopeId) ?? NOTHING;

  function edit(scopeId: string, change: Partial<Row>) {
    setRows((current) => {
      const next = new Map(current);
      const before = next.get(scopeId) ?? NOTHING;
      const after = { ...before, ...change };

      /*
       * A currency appears the moment an amount does, and only then.
       *
       * Prefilling every empty row would make a screen full of `RON` boxes that mean nothing, and a
       * currency with no amount beside it reads as a minimum somebody forgot to finish.
       */
      if (change.amount !== undefined && before.amount === "" && after.currencyCode === "") {
        after.currencyCode = suggestedCurrency;
      }

      next.set(scopeId, after);

      return next;
    });
  }

  /** The rows that will actually be sent: an amount is what makes a minimum exist. */
  const written = useMemo(() => {
    const written: { scopeId: string; isChannel: boolean; row: Row }[] = [];

    for (const channel of channels) {
      const current = rows.get(channel.id);
      if (current?.amount.trim()) written.push({ scopeId: channel.id, isChannel: true, row: current });
    }

    for (const outlet of outlets) {
      const current = rows.get(outlet.id);
      if (current?.amount.trim()) written.push({ scopeId: outlet.id, isChannel: false, row: current });
    }

    return written;
  }, [channels, outlets, rows]);

  /*
   * Checked here as well as on the server, so the message lands under the field rather than arriving
   * as a refusal about a row an author has to count to find. `looksLikeAnAmount` is the shared shape
   * — it refuses `"12,50"` rather than reading it as 12.50, because invariant parsing would make it
   * **1250** if thousands separators were allowed.
   */
  const malformed = useMemo(
    () =>
      new Set(
        written
          .filter(
            ({ row }) =>
              !looksLikeAnAmount(row.amount) ||
              Number(row.amount) <= 0 ||
              !/^[A-Za-z]{3}$/.test(row.currencyCode.trim()),
          )
          .map(({ scopeId }) => scopeId),
      ),
    [written],
  );

  const save = useMutation({
    mutationFn: () =>
      setOrderMinimums(
        accessToken!,
        written.map(
          ({ scopeId, isChannel, row }): OrderMinimumWrite => ({
            channelId: isChannel ? scopeId : null,
            outletId: isChannel ? null : scopeId,
            amount: row.amount.trim(),
            currencyCode: row.currencyCode.trim().toUpperCase(),
          }),
        ),
      ),

    onSuccess: async () => {
      setRefused([]);
      await client.invalidateQueries({ queryKey: ["order-minimums"] });
    },

    onError: (error) =>
      setRefused(
        error instanceof ApiError && error.problems.length > 0
          ? refusalTexts(refusals, error.problems)
          : [t("saveFailed")],
      ),
  });

  const dirty = useMemo(() => {
    if (written.length !== seeded.size) return true;

    return written.some(({ scopeId, row }) => {
      const before = seeded.get(scopeId);

      return (
        !before ||
        before.amount !== row.amount.trim() ||
        before.currencyCode !== row.currencyCode.trim().toUpperCase()
      );
    });
  }, [written, seeded]);

  return (
    <div className="flex flex-col gap-6">
      <p className="text-sm text-muted-foreground" role="status">
        {written.length === 0 ? t("noneSet") : t("count", { count: written.length })}
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
              <AmountRow
                key={channel.id}
                scopeId={channel.id}
                label={channel.name}
                value={row(channel.id)}
                malformed={malformed.has(channel.id)}
                canWrite={canWrite}
                onChange={(change) => edit(channel.id, change)}
              />
            ))}
          </ul>
        )}
      </section>

      <section className="flex flex-col gap-2">
        <h2 className="text-sm font-semibold">{t("outlets")}</h2>
        <p className="text-xs text-muted-foreground">{t("outletsHint")}</p>

        {outlets.length === 0 ? (
          <p className="text-sm text-muted-foreground">{t("noOutlets")}</p>
        ) : (
          <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
            {outlets.map((outlet) => (
              <AmountRow
                key={outlet.id}
                scopeId={outlet.id}
                label={`${outlet.name} · ${outlet.code}`}
                value={row(outlet.id)}
                malformed={malformed.has(outlet.id)}
                canWrite={canWrite}
                onChange={(change) => edit(outlet.id, change)}
              />
            ))}
          </ul>
        )}

        <OutletPicker
          chosen={outlets}
          onChange={setOutlets}
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
            disabled={!dirty || malformed.size > 0 || save.isPending}
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

/**
 * One scope's amount and currency.
 *
 * The two boxes sit together because they are one fact: `BR-ORD-7` compares an order's total against
 * this pair, and a number without its currency is not a threshold. The currency is a plain text box
 * rather than a select — the tenant's own price lists are the vocabulary, and there is no endpoint
 * that enumerates ISO-4217.
 */
function AmountRow({
  scopeId,
  label,
  value,
  malformed,
  canWrite,
  onChange,
}: {
  scopeId: string;
  label: string;
  value: Row;
  malformed: boolean;
  canWrite: boolean;
  onChange: (change: Partial<Row>) => void;
}) {
  const t = useTranslations("OrderMinimums");

  return (
    <li className="flex flex-wrap items-center gap-3 px-4 py-2 text-sm">
      <span className="min-w-40 flex-1">{label}</span>

      <input
        id={`amount-${scopeId}`}
        // `inputMode`, never `type="number"`: a numeric input hands back a `number` on some
        // browsers, which is the one coercion `BR-PRD-8` forbids on exactly this value.
        inputMode="decimal"
        className="w-28 rounded-md border border-border px-2 py-1 text-right"
        disabled={!canWrite}
        value={value.amount}
        placeholder={t("noMinimum")}
        aria-label={t("amountFor", { scope: label })}
        aria-invalid={malformed || undefined}
        onChange={(event) => onChange({ amount: event.target.value })}
      />

      <input
        id={`currency-${scopeId}`}
        className="w-16 rounded-md border border-border px-2 py-1 uppercase"
        disabled={!canWrite}
        maxLength={3}
        value={value.currencyCode}
        aria-label={t("currencyFor", { scope: label })}
        aria-invalid={malformed || undefined}
        onChange={(event) => onChange({ currencyCode: event.target.value })}
      />
    </li>
  );
}
