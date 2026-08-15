"use client";

import {
  BadgePercent,
  Boxes,
  CalendarDays,
  ClipboardList,
  Coins,
  Gauge,
  Grid2x2,
  Map,
  Package,
  Repeat,
  SlidersHorizontal,
  Store,
  Tag,
  Tags,
  Upload,
  Users,
} from "lucide-react";
import { useTranslations } from "next-intl";

import {
  findScreen,
  type NavScreenKey,
  visibleScreens,
} from "@/components/back-office/navigation";
import { Link, usePathname } from "@/i18n/navigation";
import { usePermissions } from "@/lib/auth/use-permissions";
import { cn } from "@/lib/utils";

/**
 * The screens of the section you are standing in (W12½ slice 3).
 *
 * **The second level the sidebar never had.** Of the seventeen screens that deserve a navigation
 * item, six had one; the other eleven lived in a row of outline buttons on a section index, so
 * reaching price lists meant landing on the catalogue first and spotting the right button, and
 * standing *on* price lists offered no way sideways at all.
 *
 * **Additive for one slice.** The five `*-actions.tsx` rows still render and still hold the same
 * links — slice 4 deletes them. Duplicating the links for one PR is the price of never having a
 * commit on `main` where a screen is unreachable, which is the property this whole redesign is
 * about.
 *
 * **It does not know the route tree**, only the model: `findScreen` answers which screen owns the
 * current path, including the eleven record-detail routes that have no item of their own, so the
 * panel stays put and stays lit while somebody edits a price list four segments deep.
 */

/**
 * One icon per screen, and every one of them already chosen.
 *
 * These are the icons the action rows import today — `CalendarDays` for the working calendar,
 * `BadgePercent` for promotions, `Upload` for the importer. Lifting them here rather than picking
 * afresh means slice 4 deletes those rows without deleting a decision, and the panel looks like the
 * thing it replaces. Two are new, both for single-screen sections that had no row to lift from, and
 * both are the section's own icon: `Grid2x2` and `Users`.
 *
 * `Package` is the one genuine choice. The catalogue's row had no icon of its own — it *was* the
 * page — and reusing `Boxes` would have put the same glyph on the catalogue and on assortments.
 */
const ICONS: Record<NavScreenKey, React.ComponentType<{ className?: string }>> = {
  journeyPlans: Map,
  callFrequency: Repeat,
  workingCalendars: CalendarDays,
  outletList: Store,
  channels: Tags,
  customFields: SlidersHorizontal,
  outletImport: Upload,
  catalogue: Package,
  classification: Tags,
  assortments: Boxes,
  priceLists: Tag,
  promotions: BadgePercent,
  orderMinimums: Coins,
  territoryList: Grid2x2,
  scoreWeights: Gauge,
  surveys: ClipboardList,
  userList: Users,
};

export function SectionPanel() {
  const t = useTranslations("Nav");
  const pathname = usePathname();
  const { has } = usePermissions();

  const here = findScreen(pathname);

  // Outside the back office entirely — sign-in, the field app, a 404. Nothing to be beside.
  if (!here) return null;

  const screens = visibleScreens(here.item, has);

  /*
   * **A panel repeating the rail item above it is a dead control**, and this codebase rejects those
   * everywhere else — Territories and Users have one screen each, so their panel would be a 192px
   * column saying what the sidebar already said.
   *
   * The cost is a layout that shifts by a column when you move between a section with screens and
   * one without. That is visible and it is the better trade: the alternative is a permanent column
   * that is empty of information two sections out of nine, which reads as something failing to load.
   *
   * Fewer than two rather than zero, because permissions can reduce a six-screen section to one —
   * somebody holding `channel:read` alone sees exactly one screen under Outlets, and the panel
   * should not appear just to tell them where they already are.
   */
  if (screens.length < 2) return null;

  return (
    <nav
      // Named for its section rather than "section navigation", because a screen reader listing
      // landmarks should be able to tell this apart from the sidebar without entering either.
      aria-label={t(`items.${here.item.key}`)}
      // One shape at every width (W12½ slice 6), for the reason the rail gives: the drawer is what
      // changes below `md`, not the column inside it.
      className="flex w-48 shrink-0 flex-col gap-1 border-r border-border bg-background p-3 md:h-dvh"
    >
      <span className="px-2.5 pb-2 text-sm font-semibold">{t(`items.${here.item.key}`)}</span>

      {screens.map((screen) => {
        const Icon = ICONS[screen.key];
        const active = screen.key === here.screen.key;

        return (
          <Link
            key={screen.key}
            href={screen.href}
            aria-current={active ? "page" : undefined}
            className={cn(
              "flex shrink-0 items-center gap-2.5 rounded-lg px-2.5 py-2 text-sm transition-colors",
              active
                ? "bg-accent font-semibold text-accent-foreground"
                : "text-muted-foreground hover:bg-accent/50 hover:text-foreground",
            )}
          >
            <Icon className="size-4 shrink-0" />
            <span className="truncate">{t(`screens.${screen.key}`)}</span>
          </Link>
        );
      })}
    </nav>
  );
}
