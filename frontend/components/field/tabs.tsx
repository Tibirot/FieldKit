"use client";

import { CalendarDays, Smartphone, Store } from "lucide-react";
import { useTranslations } from "next-intl";

import { Link, usePathname } from "@/i18n/navigation";
import { cn } from "@/lib/utils";

/**
 * Where a rep can go, from anywhere (W12½ slice 8a).
 *
 * **The field app had no navigation at all.** Six screens, two links in the header, and everything
 * else a linear flow: a rep four levels into an audit left it with `router.replace`, and a shop that
 * today's round does not name had no door but the unplanned-call picker — which hides every shop
 * already planned, so it is useless for looking one up.
 *
 * **Three places, and each is somewhere.** Today is the round; Outlets is the territory, which this
 * slice also builds; the device screen answers "has my work gone in" and used to *be* the home
 * screen. Sync is not here on purpose — see the header.
 *
 * **A bar rather than a drawer**, unlike the back office. Nine sections need a drawer; three do not,
 * and a rep works one-handed with a phone in a shop — the bottom edge is the reachable one, and a
 * tap that needs a menu opened first is two taps.
 */

const TABS = [
  { key: "today", href: "/field", icon: CalendarDays },
  { key: "outlets", href: "/field/outlets", icon: Store },
  { key: "device", href: "/field/device", icon: Smartphone },
] as const;

/**
 * Whether this tab owns the current path.
 *
 * **`/field` is matched exactly and the others by prefix**, which is not an inconsistency but the
 * only reading that works: every field route begins with `/field`, so a prefix match would light
 * *Today* on the outlet list, the device screen and every visit. The segment boundary on the others
 * is the rule the back office already settled — a future `/field/outlets-archive` is not Outlets.
 */
function owns(href: string, pathname: string): boolean {
  if (href === "/field") return pathname === "/field";

  return pathname === href || pathname.startsWith(`${href}/`);
}

export function FieldTabs() {
  const t = useTranslations("Field.tabs");
  const pathname = usePathname();

  return (
    <nav
      aria-label={t("label")}
      // `pb-[env(safe-area-inset-bottom)]` because reps use this on phones with a home indicator,
      // and the layout is already `viewportFit: "cover"` — without it the last row of the bar sits
      // under the gesture bar and the taps land on the operating system instead.
      className="flex shrink-0 border-t border-border bg-background pb-[env(safe-area-inset-bottom)]"
    >
      {TABS.map(({ key, href, icon: Icon }) => {
        const here = owns(href, pathname);

        return (
          <Link
            key={key}
            href={href}
            aria-current={here ? "page" : undefined}
            className={cn(
              "flex flex-1 flex-col items-center gap-1 py-2 text-[11px] font-medium transition-colors",
              here ? "text-primary" : "text-muted-foreground hover:text-foreground",
            )}
          >
            <Icon className="size-5 shrink-0" />
            <span className="truncate">{t(key)}</span>
          </Link>
        );
      })}
    </nav>
  );
}
