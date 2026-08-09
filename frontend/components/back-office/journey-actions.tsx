"use client";

import { CalendarDays, Repeat } from "lucide-react";
import { useTranslations } from "next-intl";

import { LinkButton } from "@/components/ui/link-button";
import { usePermissions } from "@/lib/auth/use-permissions";

/** Which journey screen is being looked at, so it is not offered as a way to reach itself. */
export type JourneyScreen = "frequencies" | "calendars";

/**
 * The way between the journey screens.
 *
 * The section has no landing page of its own, deliberately: an index whose only content is two links
 * is a page that exists to be clicked through. So each screen carries the way to the others, which
 * is the same shape `ProductActions` takes and the same reason — the sidebar names sections, not the
 * screens inside them.
 *
 * **Gated on `journey:read`**, like the nav item. Everything a reader can reach here refuses its own
 * write controls, so there is nothing to hide behind a second permission.
 */
export function JourneyActions({ current }: { current: JourneyScreen }) {
  const t = useTranslations("Journeys");
  const { has } = usePermissions();

  if (!has("journey:read")) return null;

  return (
    <div className="flex flex-wrap gap-2">
      {current === "frequencies" ? null : (
        <LinkButton href="/journeys/frequencies" size="sm" variant="outline">
          <Repeat className="size-4" />
          {t("callFrequency")}
        </LinkButton>
      )}

      {current === "calendars" ? null : (
        <LinkButton href="/journeys/calendars" size="sm" variant="outline">
          <CalendarDays className="size-4" />
          {t("workingCalendar")}
        </LinkButton>
      )}
    </div>
  );
}
