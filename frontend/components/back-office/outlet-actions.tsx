"use client";

import { Plus, SlidersHorizontal, Tags, Upload } from "lucide-react";
import { useTranslations } from "next-intl";

import { LinkButton } from "@/components/ui/link-button";
import { usePermissions } from "@/lib/auth/use-permissions";

/**
 * What an admin does to the outlet base, for someone who may.
 *
 * A client component only because the permission answer comes from the API rather than from the
 * request: the page around it stays server-rendered, and this is the smallest piece that has to know
 * who is asking.
 *
 * **Channels are their own permission**, and deliberately: `channel:write` is what the importer
 * pointedly does not hold, so that a typo in one cell cannot mint "Modren Trade" as a permanent
 * classification that assortment and pricing key off. Someone may well maintain outlets without
 * being trusted to invent the vocabulary they are filed under.
 *
 * **Custom fields are a third**, held by Configuration rather than Outlets, and the separation says
 * the same thing one level up: maintaining outlets is not the same authority as deciding what an
 * outlet *is*. A definition added here changes what every outlet must carry and what the import
 * will refuse.
 */
export function OutletActions() {
  const t = useTranslations("Outlets");
  const { has } = usePermissions();

  if (!has("outlet:write") && !has("channel:read") && !has("config:read")) return null;

  return (
    <div className="flex flex-wrap gap-2">
      {has("config:read") ? (
        <LinkButton href="/outlets/custom-fields" size="sm" variant="outline">
          <SlidersHorizontal className="size-4" />
          {t("manageCustomFields")}
        </LinkButton>
      ) : null}

      {has("channel:read") ? (
        <LinkButton href="/outlets/channels" size="sm" variant="outline">
          <Tags className="size-4" />
          {t("manageChannels")}
        </LinkButton>
      ) : null}

      {has("outlet:write") ? (
        <>
          <LinkButton href="/outlets/import" size="sm" variant="outline">
            <Upload className="size-4" />
            {t("importOutlets")}
          </LinkButton>
          <LinkButton href="/outlets/new" size="sm">
            <Plus className="size-4" />
            {t("newOutlet")}
          </LinkButton>
        </>
      ) : null}
    </div>
  );
}
