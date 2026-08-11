"use client";

import { ClipboardList, Gauge } from "lucide-react";
import { useTranslations } from "next-intl";

import { LinkButton } from "@/components/ui/link-button";
import { usePermissions } from "@/lib/auth/use-permissions";

/**
 * Moving between the Configuration screens.
 *
 * <b>The section has more than one screen and the nav has one entry for it</b>, which is the shape
 * every other section here already has: `Outlets` reaches custom fields and channels through
 * `OutletActions`, `Products` reaches price lists and promotions the same way. A second sidebar
 * level would be the alternative, and it would have to be built for one section that has two pages.
 *
 * Rendered on both screens rather than only on a section index, because there is no section index —
 * the nav goes straight to the weights, so this is the only route between the two.
 *
 * The current screen still renders its own link, unfiltered by pathname. A row that changed shape as
 * you moved through it makes the *other* item shift position, and a control that is somewhere else
 * each time you look for it is worse than one that is occasionally redundant.
 */
export function ConfigurationActions() {
  const t = useTranslations("Configuration");
  const { has } = usePermissions();

  // Both screens are gated on the same permission, so there is nothing to split: someone who may
  // read one may read the other, and someone who may read neither is not on this page.
  if (!has("config:read")) return null;

  return (
    <div className="flex flex-wrap gap-2">
      <LinkButton href="/configuration/score-weights" size="sm" variant="outline">
        <Gauge className="size-4" />
        {t("weights")}
      </LinkButton>
      <LinkButton href="/configuration/surveys" size="sm" variant="outline">
        <ClipboardList className="size-4" />
        {t("surveys")}
      </LinkButton>
    </div>
  );
}
