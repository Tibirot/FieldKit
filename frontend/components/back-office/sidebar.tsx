"use client";

import {
  BarChart3,
  Boxes,
  ClipboardList,
  Grid2x2,
  Route,
  ShoppingCart,
  SlidersHorizontal,
  Store,
  Users,
} from "lucide-react";
import { useTranslations } from "next-intl";

import {
  coversPath,
  isSectionVisible,
  landingFor,
  NAVIGATION,
  type NavItem,
  type NavKey,
} from "@/components/back-office/navigation";
import { Link, usePathname } from "@/i18n/navigation";
import { usePermissions } from "@/lib/auth/use-permissions";
import { cn } from "@/lib/utils";

/**
 * The section rail (W12½ slice 4).
 *
 * **Nine sections, always in the same place, never scrolling.** Picking one is the whole of what it
 * does: where that lands and what else is in there are the section panel's job, which is why this is
 * 68px wide instead of 224 and why the two together are still narrower than the sidebar it replaces
 * was on its own once the panel joined it.
 *
 * **The three fields it used to read are gone**, and that is the point of the shape rather than a
 * tidy-up. `item.href` said where to navigate, `item.section` which prefix to highlight on, and
 * `item.permissions` who may see it — all three now derived from the section's screens, so the rail
 * cannot send somebody to a screen the panel does not list or hide a section whose screens they can
 * open. `Journeys` and `Configuration` used to point *into* themselves because neither had an index
 * worth landing on; they now land on their first visible screen like everything else.
 */

const ICONS: Record<NavKey, React.ComponentType<{ className?: string }>> = {
  dashboard: BarChart3,
  journeys: Route,
  visits: ClipboardList,
  orders: ShoppingCart,
  outlets: Store,
  products: Boxes,
  territories: Grid2x2,
  configuration: SlidersHorizontal,
  users: Users,
};

/** Icon over label, both centred — the rail is too narrow for a row, and a bare icon says less. */
const SHARED =
  "flex w-full flex-col items-center gap-1 rounded-lg px-1 py-2 text-center text-[10px] leading-tight font-medium transition-colors";

function RailItem({ item, active, href }: { item: NavItem; active: boolean; href?: string }) {
  const t = useTranslations("Nav");
  const Icon = ICONS[item.key];

  const { soon } = item;

  if (soon) {
    return (
      // A real <span aria-disabled> rather than a disabled link: there is nowhere to go, so it must
      // not be focusable or announced as a link. The week is in the title *and* in visible text —
      // a tooltip alone would be invisible to a keyboard or a screen reader.
      <span
        aria-disabled="true"
        className={cn(SHARED, "cursor-not-allowed text-muted-foreground/55")}
        title={t(`soon.${soon}`)}
      >
        <Icon className="size-5 shrink-0" />
        <span className="line-clamp-2">{t(`items.${item.key}`)}</span>
        <span className="rounded border border-border px-1 font-mono text-[9px] leading-4">
          {t(`soon.${soon}`)}
        </span>
      </span>
    );
  }

  /*
   * Only ever undefined for a section with no screen this caller may open — and `isSectionVisible`
   * has already refused to draw that. Returning null rather than asserting, because a crash in the
   * navigation takes the whole console with it and an item quietly missing is the same outcome the
   * permission rules ask for anyway.
   */
  if (href === undefined) return null;

  return (
    <Link
      href={href}
      aria-current={active ? "page" : undefined}
      className={cn(
        SHARED,
        active
          ? "bg-accent font-semibold text-accent-foreground"
          : "text-muted-foreground hover:bg-accent/50 hover:text-foreground",
      )}
    >
      <Icon className="size-5 shrink-0" />
      <span className="line-clamp-2">{t(`items.${item.key}`)}</span>
    </Link>
  );
}

export function Sidebar({ workspace }: { workspace: string | null }) {
  const t = useTranslations("Nav");
  const pathname = usePathname();
  const { has } = usePermissions();

  return (
    <nav
      aria-label={t("label")}
      className={cn(
        "flex shrink-0 flex-row gap-1 overflow-x-auto border-b border-border bg-muted/40 p-2",
        "md:h-dvh md:w-[68px] md:flex-col md:overflow-x-visible md:border-r md:border-b-0",
      )}
    >
      {/*
        The mark keeps the workspace in its tooltip rather than beside it. 68px has no room for a
        tenant name, and dropping it entirely would take away the one thing on screen that says
        which tenant you are looking at.
      */}
      <span
        title={workspace ?? t("noWorkspace")}
        className="mb-1 grid size-9 shrink-0 place-items-center self-center rounded-lg bg-primary text-primary-foreground"
      >
        <Route className="size-4" />
        <span className="sr-only">
          {t("brand")} — {workspace ?? t("noWorkspace")}
        </span>
      </span>

      {NAVIGATION.map((group) => {
        const items = group.items.filter((item) => isSectionVisible(item, has));

        if (items.length === 0) return null;

        return (
          <div key={group.key ?? "top"} className="contents">
            {/*
              The group heading is a hairline on the rail rather than a word. "MASTER DATA" does not
              fit in 68px and abbreviating it invents a label nobody chose — the grouping is still
              worth showing, so it is shown as a separation instead of said. Named for screen
              readers, which are not short of room.
            */}
            {group.key ? (
              <span
                role="separator"
                aria-label={t(`groups.${group.key}`)}
                className="my-1 h-px w-8 shrink-0 self-center bg-border md:w-8"
              />
            ) : null}
            {items.map((item) => (
              <RailItem
                key={item.key}
                item={item}
                active={coversPath(item, pathname)}
                href={landingFor(item, has)}
              />
            ))}
          </div>
        );
      })}
    </nav>
  );
}
