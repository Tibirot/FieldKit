"use client";

import { Boxes, Tag, Tags } from "lucide-react";
import { useTranslations } from "next-intl";

import { Button } from "@/components/ui/button";
import { Link } from "@/i18n/navigation";
import { usePermissions } from "@/lib/auth/use-permissions";

/**
 * The way out of the catalogue and into how it is classified.
 *
 * A client component only because the permission answer comes from the API rather than the request:
 * the page around it stays server-rendered, and this is the smallest piece that has to know who is
 * asking — the same shape as <c>OutletActions</c>.
 *
 * **Gated on `product:read`, not `product:write`.** Someone who may read the catalogue may read the
 * vocabulary it is filed under; the New/Edit/Delete controls on that screen gate themselves. Hiding
 * the whole route from a reader would make the ancestry they see on a product row unexplainable.
 *
 * Unlike outlets, there is no second permission here. `channel:write` is separate from
 * `outlet:write` because the outlet importer must not be able to mint a classification from a typo —
 * there is no product importer yet, so no equivalent split has been earned.
 */
export function ProductActions() {
  const t = useTranslations("Products");
  const { has } = usePermissions();

  if (!has("product:read")) return null;

  return (
    <div className="flex flex-wrap gap-2">
      {/* Assortments need `channel:read` as well: the screen is organised by channel, and someone
          who cannot see the channel list would get a selector with nothing in it. */}
      {has("channel:read") ? (
        <Button
          render={<Link href="/products/assortments" />}
          nativeButton={false}
          size="sm"
          variant="outline"
        >
          <Boxes className="size-4" />
          {t("manageAssortments")}
        </Button>
      ) : null}

      <Button
        render={<Link href="/products/price-lists" />}
        nativeButton={false}
        size="sm"
        variant="outline"
      >
        <Tag className="size-4" />
        {t("managePriceLists")}
      </Button>

      <Button
        render={<Link href="/products/classification" />}
        nativeButton={false}
        size="sm"
        variant="outline"
      >
        <Tags className="size-4" />
        {t("manageClassification")}
      </Button>
    </div>
  );
}
