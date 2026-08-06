"use client";

import { Plus, Upload } from "lucide-react";
import { useTranslations } from "next-intl";

import { Button } from "@/components/ui/button";
import { Link } from "@/i18n/navigation";
import { usePermissions } from "@/lib/auth/use-permissions";

/**
 * The two ways to add outlets — for someone who may.
 *
 * A client component only because the permission answer comes from the API rather than from the
 * request: the page around it stays server-rendered, and this is the smallest piece that has to
 * know who is asking.
 *
 * Both lead to a form whose save would be refused without `outlet:write`, and the import screen
 * additionally reads its own capabilities from an endpoint behind the same permission — so without
 * it the screen is an upload box that cannot answer what it accepts.
 */
export function OutletActions() {
  const t = useTranslations("Outlets");
  const { has } = usePermissions();

  if (!has("outlet:write")) return null;

  return (
    <div className="flex gap-2">
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
    </div>
  );
}
