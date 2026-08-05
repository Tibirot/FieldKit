// @vitest-environment jsdom

import { renderHook } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { describe, expect, it } from "vitest";

import { useBusinessDay } from "@/lib/dates";

/** The app's own formatter settings: UTC, per ADR-0010 and the i18n request config. */
function day(iso: string, locale = "en") {
  const { result } = renderHook(() => useBusinessDay(), {
    wrapper: ({ children }) => (
      <NextIntlClientProvider locale={locale} timeZone="UTC" messages={{}}>
        {children}
      </NextIntlClientProvider>
    ),
  });

  return result.current(iso);
}

describe("useBusinessDay", () => {
  it("renders the day it was given, not the day before", () => {
    // The bug this exists for. `new Date("2026-01-01T00:00:00")` is local midnight, and the app
    // formats in UTC — so east of Greenwich an assignment starting on 1 January displayed as
    // starting on 31 December.
    expect(day("2026-01-01")).toBe("Jan 1, 2026");
    expect(day("2026-12-31")).toBe("Dec 31, 2026");
  });

  it("runs somewhere the difference would show", () => {
    // The test above is only worth anything off UTC: under UTC, parsing a date as local midnight and
    // formatting in UTC gives back the same day, so the bug is invisible and the suite passes on a
    // CI runner while failing on a laptop. The zone is pinned in vitest.config.ts; this asserts it,
    // because a config change that quietly reverted it would take the coverage with it.
    expect(Intl.DateTimeFormat().resolvedOptions().timeZone).not.toBe("UTC");
  });

  it("writes the date the reader's language writes", () => {
    expect(day("2026-03-09", "ro")).toContain("2026");
    expect(day("2026-03-09", "ro")).not.toBe(day("2026-03-09", "en"));
  });
});
