"use client";

import { Plus } from "lucide-react";
import { useTranslations } from "next-intl";

import { LinkButton } from "@/components/ui/link-button";
import { usePermissions } from "@/lib/auth/use-permissions";

/**
 * Adding a shop, from the list it gets added to (W12½ slice 4).
 *
 * **What is left of `OutletActions` after the navigation took the rest.** That row held four links,
 * and three of them — channels, custom fields, the importer — were places, so they moved into the
 * section panel with the other seventeen screens. This one is not a place. `/outlets/new` is a form
 * reached from the list it appends to, and a **write** control besides, which is why the reachability
 * gate exempts it by name and why it would have been wrong to drop into a navigation panel: a panel
 * lists where you can go, not what you may do.
 *
 * So it lands here, beside the table, which is where it belonged before the row existed to collect
 * it. Gated on `outlet:write` exactly as it was — a reader who may not create outlets is not offered
 * a door that will not open, which is the rule the whole nav follows.
 */
export function NewOutletButton() {
  const t = useTranslations("Outlets");
  const { has } = usePermissions();

  if (!has("outlet:write")) return null;

  return (
    <LinkButton href="/outlets/new" size="sm">
      <Plus className="size-4" />
      {t("newOutlet")}
    </LinkButton>
  );
}
