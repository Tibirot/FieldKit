"use client";

import {
  BarChart3,
  Boxes,
  ClipboardList,
  Grid2x2,
  Route,
  ShoppingCart,
  Store,
  Users,
} from "lucide-react";
import { useTranslations } from "next-intl";

import { NAVIGATION, type NavItem, type NavKey } from "@/components/back-office/navigation";
import { Link, usePathname } from "@/i18n/navigation";
import { cn } from "@/lib/utils";

const ICONS: Record<NavKey, React.ComponentType<{ className?: string }>> = {
  dashboard: BarChart3,
  journeys: Route,
  visits: ClipboardList,
  orders: ShoppingCart,
  outlets: Store,
  products: Boxes,
  territories: Grid2x2,
  users: Users,
};

function NavLink({ item, active }: { item: NavItem; active: boolean }) {
  const t = useTranslations("Nav");
  const Icon = ICONS[item.key];

  const shared =
    "flex items-center gap-2.5 rounded-lg px-2.5 py-2 text-sm font-medium transition-colors";

  // Destructured before the branch on purpose: narrowing an optional field reaches a local but not
  // `item.soon` inside a template literal, and next-intl's keys are typed strictly enough to notice.
  // Being made to write this is the type system doing its job — the alternative is a `soon.undefined`
  // lookup that renders an empty badge and never fails.
  const { href, soon } = item;

  if (soon) {
    return (
      // A real <span aria-disabled> rather than a disabled link: there is nowhere to go, so it must
      // not be focusable or announced as a link. The week is in the title *and* in visible text —
      // a tooltip alone would be invisible to a keyboard or a screen reader.
      <span
        aria-disabled="true"
        className={cn(shared, "cursor-not-allowed text-muted-foreground/55")}
        title={t(`soon.${soon}`)}
      >
        <Icon className="size-4 shrink-0" />
        <span className="truncate">{t(`items.${item.key}`)}</span>
        <span className="ml-auto rounded border border-border px-1.5 py-px font-mono text-[10px] leading-4">
          {t(`soon.${soon}`)}
        </span>
      </span>
    );
  }

  return (
    <Link
      href={href}
      aria-current={active ? "page" : undefined}
      className={cn(
        shared,
        active
          ? "bg-accent font-semibold text-accent-foreground"
          : "text-muted-foreground hover:bg-accent/50 hover:text-foreground",
      )}
    >
      <Icon className="size-4 shrink-0" />
      <span className="truncate">{t(`items.${item.key}`)}</span>
    </Link>
  );
}

export function Sidebar({ workspace }: { workspace: string | null }) {
  const t = useTranslations("Nav");
  const pathname = usePathname();

  return (
    <nav
      aria-label={t("label")}
      className="flex shrink-0 flex-col gap-0.5 border-b border-border bg-muted/40 p-3 md:h-dvh md:w-56 md:border-r md:border-b-0"
    >
      <div className="mb-2 flex items-center gap-2.5 border-b border-border px-2 pb-3.5">
        <span className="grid size-7 shrink-0 place-items-center rounded-lg bg-primary text-primary-foreground">
          <Route className="size-4" />
        </span>
        <span className="min-w-0">
          <span className="block truncate text-sm font-semibold">{t("brand")}</span>
          <span className="block truncate font-mono text-[11px] text-muted-foreground">
            {workspace ?? t("noWorkspace")}
          </span>
        </span>
      </div>

      {NAVIGATION.map((group) => (
        <div key={group.key ?? "top"} className="contents">
          {group.key ? (
            <span className="px-2.5 pt-3.5 pb-1 font-mono text-[10px] font-bold tracking-[0.12em] text-muted-foreground/70 uppercase">
              {t(`groups.${group.key}`)}
            </span>
          ) : null}
          {group.items.map((item) => (
            <NavLink
              key={item.key}
              item={item}
              active={item.href !== undefined && pathname.startsWith(item.href)}
            />
          ))}
        </div>
      ))}
    </nav>
  );
}
