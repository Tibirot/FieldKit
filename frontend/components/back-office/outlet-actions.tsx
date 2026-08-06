"use client";

import { Plus, Tags, Upload } from "lucide-react";
import { useTranslations } from "next-intl";

import { Button } from "@/components/ui/button";
import { Link } from "@/i18n/navigation";
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
 */
export function OutletActions() {
  const t = useTranslations("Outlets");
  const { has } = usePermissions();

  if (!has("outlet:write") && !has("channel:read")) return null;

  return (
    <div className="flex gap-2">
      {has("channel:read") ? (
        <Button
          render={<Link href="/outlets/channels" />}
          nativeButton={false}
          size="sm"
          variant="outline"
        >
          <Tags className="size-4" />
          {t("manageChannels")}
        </Button>
      ) : null}

      {has("outlet:write") ? (
        <>
          <Button
            render={<Link href="/outlets/import" />}
            nativeButton={false}
            size="sm"
            variant="outline"
          >
            <Upload className="size-4" />
            {t("importOutlets")}
          </Button>
          <Button render={<Link href="/outlets/new" />} nativeButton={false} size="sm">
            <Plus className="size-4" />
            {t("newOutlet")}
          </Button>
        </>
      ) : null}
    </div>
  );
}
