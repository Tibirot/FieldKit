"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";

import { Button } from "@/components/ui/button";
import { useAuth } from "@/components/auth-provider";
import { ApiError } from "@/lib/api/client";
import {
  MAXIMUM_NOTE,
  REJECTION_REASONS,
  rejectOrder,
  type Order,
  type OrderRejectionReason,
} from "@/lib/api/orders";
import { refusalTexts } from "@/lib/api/refusals";

const CONTROL =
  "h-8 rounded-lg border border-input bg-background px-2 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/**
 * Refusing an order, whole (`ORD-12`, `BR-ORD-9`) — W12 slice 6b.
 *
 * **Whole-order, never per line, and the form's shape says so.** `BR-ORD-4` denies everybody —
 * supervisor included — the right to change what a rep captured at a counter. So a supervisor picks
 * a reason and may *point at* a line; they cannot edit one. The order goes back as it was and the
 * rep re-opens it on their device, which is the only place it can be corrected.
 *
 * **The reason is required and the note is not.** Half of `F4`'s own examples need no sentence — an
 * outlet that closed during the offline window explains itself — and forcing prose there produces
 * "n/a" in a field a rep is supposed to read. What is never optional is *which* refusal it was,
 * because that is what the rep acts on.
 *
 * **It is not a confirmation dialog.** Rejection is reversible in the sense that matters: the rep
 * gets the order back and can re-submit a corrected one (`BR-ORD-9`), so a modal asking "are you
 * sure" would be ceremony over a decision the form has already made explicit by asking for a reason.
 */
export function OrderRejection({ order }: { order: Order }) {
  const t = useTranslations("Orders");
  const refusals = useTranslations("Refusals");
  const { user } = useAuth();
  const queryClient = useQueryClient();

  const [open, setOpen] = useState(false);
  const [reason, setReason] = useState<OrderRejectionReason>("OffAssortment");
  const [productId, setProductId] = useState("");
  const [note, setNote] = useState("");

  const accessToken = user?.access_token;

  const reject = useMutation({
    mutationFn: () =>
      rejectOrder(accessToken!, order.id, {
        reason,
        offendingProductId: productId || undefined,
        note: note.trim() || undefined,
      }),
    onSuccess: async () => {
      // Every list this order could be in, because rejecting moves it between them: the queue it
      // leaves and the rejected list it joins are separate queries with separate keys.
      await queryClient.invalidateQueries({ queryKey: ["orders"] });
      setOpen(false);
    },
  });

  if (!open) {
    return (
      <div>
        <Button variant="outline" size="sm" onClick={() => setOpen(true)}>
          {t("reject")}
        </Button>
      </div>
    );
  }

  return (
    <form
      className="flex flex-col gap-2 rounded-xl border border-border p-3"
      onSubmit={(event) => {
        event.preventDefault();
        reject.mutate();
      }}
    >
      <div className="flex flex-wrap items-center gap-2">
        <label htmlFor={`reason-${order.id}`} className="text-sm text-muted-foreground">
          {t("reasonLabel")}
        </label>
        <select
          id={`reason-${order.id}`}
          className={CONTROL}
          value={reason}
          onChange={(event) => setReason(event.target.value as OrderRejectionReason)}
        >
          {REJECTION_REASONS.map((option) => (
            <option key={option} value={option}>{t(`reason.${option}`)}</option>
          ))}
        </select>

        {/*
         * The offending line, as a pointer rather than an edit. Only the products actually on this
         * order are offered — the server refuses anything else with `order.rejection.unknownLine`,
         * and a control that could produce that refusal would be a control that wastes a trip.
         */}
        <label htmlFor={`line-${order.id}`} className="text-sm text-muted-foreground">
          {t("lineLabel")}
        </label>
        <select
          id={`line-${order.id}`}
          className={CONTROL}
          value={productId}
          onChange={(event) => setProductId(event.target.value)}
        >
          <option value="">{t("noLine")}</option>
          {order.lines.map((line) => (
            <option key={line.productId} value={line.productId}>
              {t("lineOption", { quantity: line.quantity, unit: line.unitOfMeasure })}
            </option>
          ))}
        </select>
      </div>

      <label htmlFor={`note-${order.id}`} className="text-sm text-muted-foreground">
        {t("noteLabel")}
      </label>
      <textarea
        id={`note-${order.id}`}
        className="min-h-16 rounded-lg border border-input bg-background p-2 text-sm text-foreground focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
        maxLength={MAXIMUM_NOTE}
        value={note}
        onChange={(event) => setNote(event.target.value)}
        placeholder={t("notePlaceholder")}
      />

      {reject.isError && (
        <p role="alert" className="text-sm text-destructive">
          {/*
           * The 409 is the one worth spelling out: it means **somebody else got there first**, which
           * is not a mistake the reader made and not something retrying will fix. "Try again" would
           * be the wrong instruction — the list is what needs refreshing.
           *
           * Written inline rather than in a helper because a helper would have to take `t`, and
           * next-intl's `Translator` does not fit a `(key: string) => string` parameter — the same
           * typecheck the dashboard hit in slice 4, avoided here by not passing it anywhere.
           */}
          {!(reject.error instanceof ApiError)
            ? t("rejectFailed")
            : reject.error.status === 409
              ? t("alreadyDecided")
              : refusalTexts(refusals, reject.error.problems).join(" ") || t("rejectFailed")}
        </p>
      )}

      <div className="flex flex-wrap gap-2">
        <Button type="submit" size="sm" disabled={reject.isPending}>
          {reject.isPending ? t("rejecting") : t("confirmReject")}
        </Button>
        <Button
          type="button"
          variant="ghost"
          size="sm"
          onClick={() => setOpen(false)}
          disabled={reject.isPending}
        >
          {t("cancel")}
        </Button>
      </div>
    </form>
  );
}
