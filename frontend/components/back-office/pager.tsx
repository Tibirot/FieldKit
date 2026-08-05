"use client";

import { ChevronLeft, ChevronRight } from "lucide-react";
import { useTranslations } from "next-intl";

import { Button } from "@/components/ui/button";

/**
 * Previous / next, and where you are.
 *
 * No numbered page buttons. They are the reason offset paging was chosen over a cursor, so this is
 * where they would go — but a jump control is only useful once someone knows which page they want,
 * and on an outlet base sorted by code that is almost never true. Search and filters are how people
 * actually get to a row; the pager is how they walk the last few. Numbers land when a screen exists
 * that someone pages deeply through.
 */
export function Pager({
  page,
  pageSize,
  total,
  onChange,
}: {
  page: number;
  pageSize: number;
  total: number;
  onChange: (page: number) => void;
}) {
  const t = useTranslations("Outlets");

  const pages = Math.max(Math.ceil(total / pageSize), 1);
  const from = (page - 1) * pageSize + 1;
  const to = Math.min(page * pageSize, total);

  return (
    <div className="flex flex-wrap items-center justify-between gap-3 border-t border-border px-3.5 py-2.5">
      <p className="text-xs text-muted-foreground">
        {/* The count, and the page, in one sentence rather than as three numbers to assemble. */}
        {t("showing", { from, to, total })}
      </p>

      <div className="flex items-center gap-2">
        <span className="text-xs text-muted-foreground tabular-nums">
          {t("pageOf", { page, pages })}
        </span>

        <Button
          variant="outline"
          size="sm"
          disabled={page <= 1}
          onClick={() => onChange(page - 1)}
          aria-label={t("previousPage")}
        >
          <ChevronLeft className="size-4" />
        </Button>

        <Button
          variant="outline"
          size="sm"
          // Against the total rather than against a full page: a last page that happens to be exactly
          // `pageSize` rows would otherwise offer a next page that is always empty.
          disabled={page >= pages}
          onClick={() => onChange(page + 1)}
          aria-label={t("nextPage")}
        >
          <ChevronRight className="size-4" />
        </Button>
      </div>
    </div>
  );
}
