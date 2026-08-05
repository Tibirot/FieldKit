"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { CustomFields } from "@/components/back-office/custom-fields";
import { Button } from "@/components/ui/button";
import { useRouter } from "@/i18n/navigation";
import { ApiError } from "@/lib/api/client";
import { channelsKey, fetchChannels } from "@/lib/api/channels";
import { fetchFieldDefinitions, fieldDefinitionsKey } from "@/lib/api/field-definitions";
import {
  createOutlet,
  updateOutlet,
  type CreateOutlet,
  type OutletDetail,
  type OutletWrite,
} from "@/lib/api/outlets";

const CONTROL =
  "h-9 w-full rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/**
 * The IANA zones this browser knows.
 *
 * From the platform rather than a bundled list: a hard-coded set goes stale every time a country
 * changes its rules, and the API validates against the *runtime's* database anyway — so taking the
 * options from the same source that will judge them is the only way the list cannot be wrong.
 */
function zones(): string[] {
  return typeof Intl.supportedValuesOf === "function" ? Intl.supportedValuesOf("timeZone") : [];
}

function Field({
  label,
  htmlFor,
  children,
  required,
}: {
  label: string;
  htmlFor: string;
  children: React.ReactNode;
  required?: boolean;
}) {
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={htmlFor} className="text-sm font-medium">
        {label}
        {required ? (
          <span aria-hidden="true" className="ml-1 text-destructive">
            *
          </span>
        ) : null}
      </label>
      {children}
    </div>
  );
}

/**
 * Create or edit an outlet (`OUT-01`, `OUT-02`).
 *
 * One component for both, because they are the same form with one field's worth of difference: a
 * code is set at creation and never again (`Outlet.Update` has no code parameter — it is the
 * identifier every territory membership and import file already refers to).
 *
 * **Status is not here.** Moving an outlet through its lifecycle is its own endpoint and its own
 * decision (`OUT-04`); putting it in this form would let a careless edit close a shop as a side
 * effect of fixing a typo.
 */
export function OutletForm({ outlet }: { outlet?: OutletDetail }) {
  const t = useTranslations("OutletForm");
  const router = useRouter();
  const client = useQueryClient();
  const { user } = useAuth();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;

  const channels = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey: channelsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchChannels(accessToken!, signal),
  });

  const definitions = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey: fieldDefinitionsKey(subject ?? "", "Outlet"),
    queryFn: ({ signal }) => fetchFieldDefinitions(accessToken!, "Outlet", signal),
  });

  const [custom, setCustom] = useState<Record<string, unknown>>(outlet?.customFields ?? {});
  const [refused, setRefused] = useState<readonly string[]>([]);

  const save = useMutation({
    mutationFn: (body: CreateOutlet | OutletWrite) =>
      outlet
        ? updateOutlet(accessToken!, outlet.id, body)
        : createOutlet(accessToken!, body as CreateOutlet),

    onSuccess: async (saved) => {
      // Every list is now wrong — a rename changes a row, a channel change moves it between filters.
      // Invalidating the prefix covers every page and filter combination without enumerating them.
      await client.invalidateQueries({ queryKey: ["outlets"] });
      router.push(`/outlets/${saved.id}`);
    },

    onError: (error) =>
      setRefused(error instanceof ApiError ? error.problems : [t("failed")]),
  });

  function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setRefused([]);

    const form = new FormData(event.currentTarget);
    const text = (name: string) => {
      const value = form.get(name);
      return typeof value === "string" && value.trim() !== "" ? value.trim() : null;
    };

    const latitude = text("latitude");
    const longitude = text("longitude");

    const body: OutletWrite = {
      name: text("name") ?? "",
      channelId: text("channelId") ?? "",
      segment: text("segment"),
      banner: text("banner"),
      timeZoneId: text("timeZoneId") ?? "",

      // An address of all-nulls is no address. Sending one would store a row of empties that reads
      // as "we know nothing about where this is" in exactly the way `null` already does.
      address:
        text("street") || text("city") || text("postalCode") || text("countryCode")
          ? {
              street: text("street"),
              city: text("city"),
              postalCode: text("postalCode"),
              countryCode: text("countryCode"),
            }
          : null,

      // Both or neither — the server refuses half a coordinate, and sending one would be asking to
      // be told so.
      location:
        latitude && longitude
          ? { latitude: Number(latitude), longitude: Number(longitude) }
          : null,

      customFields: custom,
    };

    save.mutate(outlet ? body : ({ ...body, code: text("code") ?? "" } satisfies CreateOutlet));
  }

  return (
    <form onSubmit={submit} className="flex max-w-2xl flex-col gap-4">
      {refused.length > 0 ? (
        // The server's own words. A form that replaces them with "something went wrong" throws away
        // the only description of what is actually wrong — including rules this client cannot know,
        // like a code another tenant user took a second ago.
        <ul role="alert" className="rounded-lg bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={t("code")} htmlFor="code" required={!outlet}>
          <input
            id="code"
            name="code"
            required={!outlet}
            defaultValue={outlet?.code}
            maxLength={50}
            // Not editable after creation: it is the identifier territory memberships and import
            // files already refer to, so a rename would orphan every one of them.
            readOnly={Boolean(outlet)}
            className={CONTROL + (outlet ? " cursor-not-allowed text-muted-foreground" : "")}
          />
        </Field>

        <Field label={t("name")} htmlFor="name" required>
          <input id="name" name="name" required maxLength={200} defaultValue={outlet?.name} className={CONTROL} />
        </Field>

        <Field label={t("channel")} htmlFor="channelId" required>
          <select id="channelId" name="channelId" required defaultValue={outlet?.channelId ?? ""} className={CONTROL}>
            <option value="" disabled />
            {(channels.data ?? []).map((channel) => (
              <option key={channel.id} value={channel.id}>
                {channel.name}
              </option>
            ))}
          </select>
        </Field>

        <Field label={t("timeZone")} htmlFor="timeZoneId" required>
          <select
            id="timeZoneId"
            name="timeZoneId"
            required
            defaultValue={outlet?.timeZoneId ?? ""}
            className={CONTROL}
          >
            <option value="" disabled />
            {zones().map((zone) => (
              <option key={zone} value={zone}>
                {zone}
              </option>
            ))}
          </select>
        </Field>

        <Field label={t("segment")} htmlFor="segment">
          <input id="segment" name="segment" maxLength={50} defaultValue={outlet?.segment ?? ""} className={CONTROL} />
        </Field>

        <Field label={t("banner")} htmlFor="banner">
          <input id="banner" name="banner" maxLength={100} defaultValue={outlet?.banner ?? ""} className={CONTROL} />
        </Field>
      </div>

      <fieldset className="grid gap-4 rounded-xl border border-border p-4 sm:grid-cols-2">
        <legend className="px-1 text-xs font-semibold text-muted-foreground uppercase">
          {t("location")}
        </legend>

        <Field label={t("street")} htmlFor="street">
          <input id="street" name="street" maxLength={200} defaultValue={outlet?.address?.street ?? ""} className={CONTROL} />
        </Field>

        <Field label={t("city")} htmlFor="city">
          <input id="city" name="city" maxLength={100} defaultValue={outlet?.address?.city ?? ""} className={CONTROL} />
        </Field>

        <Field label={t("postalCode")} htmlFor="postalCode">
          <input id="postalCode" name="postalCode" maxLength={20} defaultValue={outlet?.address?.postalCode ?? ""} className={CONTROL} />
        </Field>

        <Field label={t("countryCode")} htmlFor="countryCode">
          <input id="countryCode" name="countryCode" maxLength={2} defaultValue={outlet?.address?.countryCode ?? ""} className={CONTROL} />
        </Field>

        <Field label={t("latitude")} htmlFor="latitude">
          {/* The bounds the shared GeoPoint enforces, so the browser refuses 91 before a request. */}
          <input id="latitude" name="latitude" type="number" step="any" min={-90} max={90} defaultValue={outlet?.location?.latitude ?? ""} className={CONTROL} />
        </Field>

        <Field label={t("longitude")} htmlFor="longitude">
          <input id="longitude" name="longitude" type="number" step="any" min={-180} max={180} defaultValue={outlet?.location?.longitude ?? ""} className={CONTROL} />
        </Field>
      </fieldset>

      <CustomFields
        definitions={definitions.data ?? []}
        values={custom}
        onChange={(key, value) =>
          setCustom((current) => {
            const next = { ...current };

            // Deleted rather than set to undefined: the whole map is sent, and an explicit
            // `"ownership": undefined` serialises to nothing while `{}` and a missing key are the
            // same thing to the server. Removing it keeps the payload honest.
            if (value === undefined) delete next[key];
            else next[key] = value;

            return next;
          })
        }
      />

      <div className="flex gap-2">
        <Button type="submit" disabled={save.isPending}>
          {save.isPending ? t("saving") : t("save")}
        </Button>
        <Button type="button" variant="outline" onClick={() => router.push("/outlets")}>
          {t("cancel")}
        </Button>
      </div>
    </form>
  );
}
