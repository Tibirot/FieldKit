"use client";

import { useQuery } from "@tanstack/react-query";
import { Boxes } from "lucide-react";
import { useTranslations } from "next-intl";
import { useParams } from "next/navigation";

import { useAuth } from "@/components/auth-provider";
import { OutletForm } from "@/components/back-office/outlet-form";
import { OutletLifecycle } from "@/components/back-office/outlet-lifecycle";
import { LinkButton } from "@/components/ui/link-button";
import { ApiError } from "@/lib/api/client";
import { fetchOutlet, outletKey } from "@/lib/api/outlets";
import { usePermissions } from "@/lib/auth/use-permissions";

/**
 * Loads one outlet, then hands it to the form.
 *
 * Separate from `OutletForm` so the form takes an outlet rather than an id — which is what lets the
 * same component serve create and edit, and what lets a test render it with a fixture instead of a
 * mocked fetch.
 */
export function OutletEditor() {
  const t = useTranslations("OutletForm");
  const { user } = useAuth();
  const { has } = usePermissions();
  const params = useParams<{ id: string }>();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;
  const id = params.id;

  const outlet = useQuery({
    enabled: Boolean(accessToken && subject && id),
    queryKey: outletKey(subject ?? "", id ?? ""),
    queryFn: ({ signal }) => fetchOutlet(accessToken!, id, signal),
  });

  if (outlet.isPending) {
    return (
      <p className="text-sm text-muted-foreground" role="status">
        {t("loading")}
      </p>
    );
  }

  if (outlet.isError) {
    // A 404 here is an outlet this tenant does not have — which is also what another tenant's id
    // looks like, and deliberately so.
    const missing = outlet.error instanceof ApiError && outlet.error.status === 404;

    return (
      <p className="text-sm text-destructive" role="alert">
        {missing ? t("notFound") : t("failed")}
      </p>
    );
  }

  return (
    <div className="flex flex-col gap-4">
      <header className="flex flex-wrap items-start gap-3">
        <div className="min-w-0 flex-1">
          <p className="font-mono text-[11.5px] text-muted-foreground">
            {t("crumbEdit", { code: outlet.data.code })}
          </p>
          <h1 className="text-lg font-semibold tracking-tight">{outlet.data.name}</h1>
        </div>

        {/* What this shop sells is a fact about it, so the way in is from here rather than from the
            catalogue. Gated on `product:read` because that is what the screen reads — someone who
            maintains outlets is not necessarily trusted with the catalogue. */}
        {has("product:read") ? (
          <LinkButton
            href={`/outlets/${outlet.data.id}/assortment`}
            size="sm"
            variant="outline"
          >
            <Boxes className="size-4" />
            {t("manageAssortment")}
          </LinkButton>
        ) : null}
      </header>
      {/*
        Keyed, so navigating from one outlet to another remounts the form.

        React Hook Form captures its defaults on first render — without this, this component stays
        mounted while the query swaps underneath it, and the second outlet is shown with the first
        one’s values in every field. Found by a test that re-rendered with a different outlet.
      */}
      <OutletForm key={outlet.data.id} outlet={outlet.data} />

      {/*
        Below the form and outside it. The API gave status its own endpoint precisely so that closing
        a shop could not ride along on an unrelated edit (`OUT-04`, spec §F4) — a control inside the
        form would put that back, with one Save covering two decisions. Only on an outlet that
        exists: there is no lifecycle to move through before there is an outlet, and the create form
        deliberately has no status field either.
      */}
      <OutletLifecycle key={`lifecycle-${outlet.data.id}`} outlet={outlet.data} />
    </div>
  );
}
