"use client";

import { useTranslations } from "next-intl";

import {
  findScreen,
  landingFor,
  NAVIGATION,
  type NavGroup,
} from "@/components/back-office/navigation";
import { Link, usePathname } from "@/i18n/navigation";
import { usePermissions } from "@/lib/auth/use-permissions";

/**
 * The trail back up, derived from the navigation (W12½ slice 5).
 *
 * **There was already a breadcrumb, and it was not one.** Every screen rendered a `crumb` string
 * from the catalog — `Master data / Products / Price lists / Scope` — inside a `<p>` with no links
 * in it. The trail was printed and could not be walked, which is why the W12½ audit first recorded
 * these screens as having none: read from the navigation's side, a trail nothing links to is
 * invisible. Twenty-seven of them, in each of two locales.
 *
 * **They had also drifted, and nothing could notice.** A second copy of a hierarchy is a copy that
 * ages: the crumbs called the journeys block *Field ops*, a group name the navigation has never had;
 * `Journeys` was *Journey planning*, `Products & pricing` was *Products*, `Perfect-store weights` was
 * *Perfect store*, and Configuration's crumbs dropped the `Admin` group entirely. A person reading
 * the trail and looking at the rail was being shown two names for one place.
 *
 * So the first three segments are derived and only the **leaf** is passed in — the part the model
 * genuinely does not know, because it is below any navigation item: a record's code, or the name of
 * a tab within a screen. Rename a section and the trail renames itself, in both languages.
 */
export function Breadcrumb({ leaf }: { leaf?: string | readonly string[] }) {
  const t = useTranslations("Nav");
  const pathname = usePathname();
  const { has } = usePermissions();

  const here = findScreen(pathname);

  // Outside the back office, or on a route no section owns. A trail that says nothing is better
  // than one that guesses, and the caller cannot be lost anywhere the navigation does not reach.
  if (!here) return null;

  const group = NAVIGATION.find((candidate: NavGroup) =>
    candidate.items.some((item) => item.key === here.item.key),
  );

  const section = t(`items.${here.item.key}`);
  const screen = t(`screens.${here.screen.key}`);
  const leaves = leaf === undefined ? [] : typeof leaf === "string" ? [leaf] : [...leaf];

  /*
   * A single-screen section names itself twice — `Territories / Territories`, `Users & roles /
   * Users & roles` — which is the same redundancy the panel refuses by not rendering at all. One of
   * the two is dropped rather than both: the section is what the rail calls it, so keeping the
   * section keeps the trail and the rail agreeing, which is the whole point of deriving it.
   */
  const trail = [
    ...(group?.key ? [{ label: t(`groups.${group.key}`) }] : []),
    { label: section, href: landingFor(here.item, has) },
    ...(screen === section ? [] : [{ label: screen, href: here.screen.href }]),
    ...leaves.map((label) => ({ label })),
  ];

  return (
    <nav aria-label={t("trail")}>
      <ol className="flex flex-wrap items-center gap-x-1.5 font-mono text-[11.5px] text-muted-foreground">
        {trail.map((crumb, index) => {
          const last = index === trail.length - 1;

          return (
            <li key={`${crumb.label}-${index}`} className="flex items-center gap-x-1.5">
              {index > 0 ? (
                // Decorative: the separator is punctuation between names, and a screen reader
                // announcing "slash" between every one of four segments is noise.
                <span aria-hidden="true" className="text-muted-foreground/50">
                  /
                </span>
              ) : null}

              {/*
                The last segment is where you already are, so it is never a link — and a group has no
                route to be one. Everything between is walkable, which is the point of the slice.
              */}
              {last || !("href" in crumb) || crumb.href === undefined ? (
                <span aria-current={last ? "page" : undefined}>{crumb.label}</span>
              ) : (
                <Link href={crumb.href} className="hover:text-foreground hover:underline">
                  {crumb.label}
                </Link>
              )}
            </li>
          );
        })}
      </ol>
    </nav>
  );
}
