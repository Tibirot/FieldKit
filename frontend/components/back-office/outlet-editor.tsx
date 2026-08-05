"use client";

import { useQuery } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useParams } from "next/navigation";

import { useAuth } from "@/components/auth-provider";
import { OutletForm } from "@/components/back-office/outlet-form";
import { ApiError } from "@/lib/api/client";
import { fetchOutlet, outletKey } from "@/lib/api/outlets";

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
      <header>
        <p className="font-mono text-[11.5px] text-muted-foreground">
          {t("crumbEdit", { code: outlet.data.code })}
        </p>
        <h1 className="text-lg font-semibold tracking-tight">{outlet.data.name}</h1>
      </header>
      <OutletForm outlet={outlet.data} />
    </div>
  );
}
