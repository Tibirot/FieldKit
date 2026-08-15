"use client";

import { useQuery } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { ApiError } from "@/lib/api/client";
import { fetchOrders, ordersKey, type OrderStatus } from "@/lib/api/orders";
import { fetchOutlets, outletsKey, type Outlet } from "@/lib/api/outlets";
import { useBusinessDay } from "@/lib/dates";

const CONTROL =
  "h-8 rounded-lg border border-input bg-background px-2 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/** The states the filter offers, in the order a supervisor works through them. */
const FILTERS: readonly (OrderStatus | "")[] = ["Submitted", "Rejected", ""];

/**
 * The queue a supervisor works through (`ORD-09`) — W12 slice 6a.
 *
 * **Submitted first, because that is the job.** The default view is what has arrived and not been
 * dealt with; the filter also offers what was rejected, so "where did that order go" is answered on
 * the screen rather than in a support thread.
 *
 * **The device's total is the order's total.** `BR-ORD-2` has the server re-price and *flag*, never
 * apply — so the figure shown is the one the rep and the shopkeeper settled on, and a server that
 * disagrees says so beside it rather than quietly replacing it. A supervisor seeing that flag is
 * looking at a pricing-data problem, not a sales one.
 *
 * Rejecting from here lands in slice 6b. The rejection is **shown** already, because an order that
 * was refused and does not say why is the thing this screen exists to prevent.
 */
export function OrderQueue() {
  const t = useTranslations("Orders");
  const { user } = useAuth();
  const day = useBusinessDay();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;
  const enabled = Boolean(accessToken && subject);

  const [status, setStatus] = useState<OrderStatus | "">("Submitted");

  const orders = useQuery({
    enabled,
    queryKey: ordersKey(subject ?? "", status || undefined),
    queryFn: ({ signal }) => fetchOrders(accessToken!, status || undefined, signal),
  });

  const outlets = useQuery({
    enabled,
    queryKey: outletsKey(subject ?? "", {}),
    queryFn: ({ signal }) => fetchOutlets(accessToken!, {}, signal),
  });

  if (orders.isError) {
    const error = orders.error;

    return (
      <p role="alert" className="text-sm text-destructive">
        {error instanceof ApiError && error.status === 403 ? t("forbidden") : t("failed")}
      </p>
    );
  }

  if (!orders.data) {
    return <p className="text-sm text-muted-foreground">{t("loading")}</p>;
  }

  const shops = new Map((outlets.data?.items ?? []).map((outlet) => [outlet.id, outlet]));

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-2">
        <label htmlFor="status" className="text-sm text-muted-foreground">{t("statusLabel")}</label>
        <select
          id="status"
          className={CONTROL}
          value={status}
          onChange={(event) => setStatus(event.target.value as OrderStatus | "")}
        >
          {FILTERS.map((option) => (
            <option key={option || "all"} value={option}>
              {option ? t(`status.${option}`) : t("allStatuses")}
            </option>
          ))}
        </select>
      </div>

      {orders.data.length === 0 ? (
        <p className="rounded-xl border border-dashed border-border px-4 py-6 text-center text-sm text-muted-foreground">
          {status === "Submitted" ? t("queueEmpty") : t("empty")}
        </p>
      ) : (
        <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
          {orders.data.map((order) => (
            <li key={order.id} className="flex flex-col gap-1 px-4 py-3">
              <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
                <span className="min-w-48 flex-1 text-sm font-medium">
                  {shops.get(order.outletId) ? naming(shops.get(order.outletId)!) : t("unknownOutlet")}
                </span>
                <span className="text-sm tabular-nums text-muted-foreground">
                  {day(order.capturedAtUtc.slice(0, 10))}
                </span>
                <span className="text-sm text-muted-foreground">
                  {t("lines", { lines: order.lines.length })}
                </span>
                <span className="text-sm font-medium tabular-nums">
                  {`${order.total.toFixed(2)} ${order.currencyCode}`}
                </span>
                <span className="text-sm">{t(`status.${order.status}`)}</span>
              </div>

              {/*
               * The server disagreeing with the device is a pricing-data problem, not a sales one —
               * and `BR-ORD-2` means the number above is still the one that stands. Saying both is
               * the only honest rendering: hiding the flag would leave a supervisor comparing a
               * total against a price list that no longer produces it.
               */}
              {order.agreement === "Differs" && order.serverTotal !== null && (
                <p className="text-xs text-amber-600 dark:text-amber-500">
                  {t("disputed", {
                    server: `${order.serverTotal.toFixed(2)} ${order.currencyCode}`,
                  })}
                </p>
              )}

              {order.rejection && (
                <p className="text-xs text-muted-foreground">
                  {t("rejected", {
                    reason: t(`reason.${order.rejection.reason}`),
                    note: order.rejection.note ?? t("noNote"),
                  })}
                </p>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/** A shop, as a person refers to it: the name, with the code that disambiguates two of them. */
function naming(outlet: Outlet): string {
  return `${outlet.name} (${outlet.code})`;
}


