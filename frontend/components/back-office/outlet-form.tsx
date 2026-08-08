"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useMemo, useState } from "react";
import { useForm, type FieldErrors, type FieldPath as RhfFieldPath } from "react-hook-form";
import { z } from "zod";

import { useAuth } from "@/components/auth-provider";
import { CustomFields } from "@/components/back-office/custom-fields";
import { OutletContacts } from "@/components/back-office/outlet-contacts";
import { Button } from "@/components/ui/button";
import { useRouter } from "@/i18n/navigation";
import { ApiError } from "@/lib/api/client";
import { channelsIncluding, channelsKey, fetchChannels } from "@/lib/api/channels";
import { fetchFieldDefinitions, fieldDefinitionsKey } from "@/lib/api/field-definitions";
import {
  createOutlet,
  updateOutlet,
  type CreateOutlet,
  type OutletContact,
  type OutletDetail,
  type OutletWrite,
} from "@/lib/api/outlets";
import { usePermissions } from "@/lib/auth/use-permissions";
import { customFieldSchema, type ValidationMessages } from "@/lib/forms/custom-field-schema";
import { zonesIncluding } from "@/lib/time-zones";
import { useValidationMessages } from "@/lib/forms/use-validation-messages";
import { cn } from "@/lib/utils";

const CONTROL =
  "h-9 w-full rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/** Trimmed, and empty becomes absent — the shape every optional string on the API expects. */
/** How the API nests custom fields — the one place its naming and this form's disagree. */
const CustomFieldPrefix = "customFields.";

/** How the API indexes a contact: `contacts[1].email`. */
const ContactPath = /^contacts\[(\d+)\]\./;

/** A path into this form, as react-hook-form names them. */
type FieldPath = RhfFieldPath<Record<string, unknown>>;

const optionalText = z
  .string()
  .trim()
  .transform((value) => (value === "" ? null : value))
  .nullable();

/**
 * The fields every outlet has, whatever a tenant added to them.
 *
 * Written out rather than derived, because unlike custom fields there is no descriptor to derive
 * from — the API's own request record is the declaration, and this is the closest a TypeScript
 * client gets to it. The lengths match its column limits; the coordinate bounds match the shared
 * `GeoPoint`.
 *
 * Every message is supplied, never left to Zod. Its defaults are developer text — *"Too small:
 * expected string to have >=1 characters"* — which is unreadable to a user and English-only in an
 * app that ships two languages.
 */
function fixedSchema(t: ReturnType<typeof useTranslations<"OutletForm">>, m: ValidationMessages) {
  const text = (max: number) =>
    z
      .string()
      .trim()
      .min(1, { message: m.required })
      .max(max, { message: m.tooLong(max) });

  return z.object({
    code: text(50),
    name: text(200),
    channelId: z.string().min(1, { message: m.required }),
    timeZoneId: z.string().min(1, { message: m.required }),
    segment: optionalText,
    banner: optionalText,
    street: optionalText,
    city: optionalText,
    postalCode: optionalText,
    countryCode: optionalText,

    // Text in, number out. An emptied number input holds `""`, and `z.number()` would reject that
    // as "expected number" for a field nobody filled in on purpose.
    latitude: coordinate(-90, 90, t("betweenLat")),
    longitude: coordinate(-180, 180, t("betweenLon")),

    // The lengths are the API's columns, checked here so a name one character too long is a message
    // under that name rather than a refusal from the server about a list.
    contacts: z.array(
      z.object({
        name: text(200),
        role: optionalText,
        phone: optionalText,
        email: optionalText.refine((value) => value === null || looksLikeAnAddress(value), {
          message: m.notAnEmail,
        }),
      }),
    ),
  });
}

/**
 * Whether this could be an email address at all.
 *
 * The same shallow rule the API applies, and deliberately so: something either side of exactly one
 * `@` and no whitespace. It catches a phone number pasted into the wrong box, which is the mistake
 * that actually happens; deliverability is only ever settled by sending mail, and a stricter pattern
 * rejects addresses that work.
 */
function looksLikeAnAddress(value: string): boolean {
  return /^[^\s@]+@[^\s@]+$/.test(value);
}

function coordinate(min: number, max: number, message: string) {
  return z
    .string()
    .trim()
    .transform((value) => (value === "" ? null : Number(value)))
    .refine((value) => value === null || (Number.isFinite(value) && value >= min && value <= max), {
      message,
    });
}

function Field({
  label,
  htmlFor,
  required,
  error,
  children,
}: {
  label: string;
  htmlFor: string;
  required?: boolean;
  error?: string;
  children: React.ReactNode;
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
      {error ? (
        <p id={`${htmlFor}-error`} className="text-xs text-destructive">
          {error}
        </p>
      ) : null}
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
  const permissions = usePermissions();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;
  const canWrite = permissions.has("outlet:write");

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

  const messages = useValidationMessages();

  // Rebuilt when the catalogue arrives, and only then. The resolver closes over this, so a schema
  // recreated every render would revalidate on every keystroke against a brand-new object.
  const schema = useMemo(
    () => fixedSchema(t, messages).extend({ custom: customFieldSchema(definitions.data ?? [], messages) }),
    [definitions.data, t, messages],
  );

  const form = useForm({
    resolver: zodResolver(schema),

    // On blur rather than on change: telling someone their code is too short while they are typing
    // the second character is noise, and `onSubmit` alone means finding out about four problems one
    // page-scroll away from the field that caused each.
    mode: "onBlur",

    defaultValues: {
      code: outlet?.code ?? "",
      name: outlet?.name ?? "",
      channelId: outlet?.channelId ?? "",
      timeZoneId: outlet?.timeZoneId ?? "",
      segment: outlet?.segment ?? "",
      banner: outlet?.banner ?? "",
      street: outlet?.address?.street ?? "",
      city: outlet?.address?.city ?? "",
      postalCode: outlet?.address?.postalCode ?? "",
      countryCode: outlet?.address?.countryCode ?? "",
      latitude: outlet?.location?.latitude?.toString() ?? "",
      longitude: outlet?.location?.longitude?.toString() ?? "",

      // What the outlet already has, so a save round-trips them. Omitting this field is not
      // "leave them alone" — see the note where the body is built.
      contacts: outlet?.contacts ?? [],
      custom: outlet?.customFields ?? {},
    },
  });

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

    onError: (error) => {
      if (!(error instanceof ApiError)) {
        setRefused([t("failed")]);
        return;
      }

      // A problem the API attached to a field goes under that control, exactly like a client-side
      // one — which is the whole reason the API names fields now. Anything it could not attribute
      // stays at the top, because a message pinned to a guessed control is worse than one that
      // admits it is about the request.
      const unattributed: string[] = [];

      for (const problem of error.problems) {
        const path = formPath(problem.field);

        // Cast because the path is a string the API chose, and TypeScript can only accept the
        // schema's literal names. `formPath` is what makes it safe — it checks against the form's
        // actual values, so a name this form has no control for never reaches here.
        if (path) form.setError(path as never, { type: "server", message: problem.message });
        else unattributed.push(problem.message);
      }

      // A refusal the API attached to nothing — a 403, a 404, a 500 with no body — still has to say
      // something. Without this the loop above runs zero times and the screen goes silent, which reads
      // as a Save button that does nothing rather than as a refusal.
      setRefused(error.problems.length > 0 ? unattributed : [t("failed")]);
    },
  });

  /**
   * The API's field path, as this form names it, or undefined if it renders no such control.
   *
   * The two agree everywhere except custom fields: the request nests them under `customFields`
   * while the form holds them under `custom`, so that one prefix is translated. Everything else is
   * checked against what is actually on screen — the fixed fields against the form's own values, a
   * custom key against the catalogue the inputs were built from — because `setError` on a path with
   * no control attached swallows the message silently, and an unknown field is a rule the API grew
   * rather than a reason to lose what it said.
   */
  function formPath(field: string | null): FieldPath | undefined {
    if (!field) return undefined;

    // `contacts[1].email` is how the request named it; react-hook-form spells the same path
    // `contacts.1.email`. Bounds-checked against the rows on screen, because an index past the end
    // is a control that does not exist and `setError` would swallow the message.
    if (ContactPath.test(field)) {
      const path = field.replace(ContactPath, "contacts.$1.");

      return Number(ContactPath.exec(field)![1]) < form.getValues("contacts").length
        ? (path as FieldPath)
        : undefined;
    }

    if (field.startsWith(CustomFieldPrefix)) {
      const key = field.slice(CustomFieldPrefix.length);

      return definitions.data?.some((definition) => definition.key === key)
        ? (`custom.${key}` as FieldPath)
        : undefined;
    }

    return field in form.getValues() ? (field as FieldPath) : undefined;
  }

  const submit = form.handleSubmit((values) => {
    setRefused([]);

    const body: OutletWrite = {
      name: values.name,
      channelId: values.channelId,
      timeZoneId: values.timeZoneId,
      segment: values.segment,
      banner: values.banner,

      // An address of all-nulls is no address. Sending one would store a row of empties that reads
      // as "we know nothing about where this is" in exactly the way `null` already does.
      address:
        values.street || values.city || values.postalCode || values.countryCode
          ? {
              street: values.street,
              city: values.city,
              postalCode: values.postalCode,
              countryCode: values.countryCode,
            }
          : null,

      // Both or neither — the server refuses half a coordinate, and sending one would be asking to
      // be told so.
      location:
        values.latitude !== null && values.longitude !== null
          ? { latitude: values.latitude, longitude: values.longitude }
          : null,

      // Always sent, even untouched. The API replaces the list wholesale, so an absent `contacts`
      // is an emptied one — which is correct for a PUT and was quietly deleting every contact on
      // every outlet this form saved, because the form neither read them nor sent them back.
      contacts: values.contacts.map(
        (contact): OutletContact => ({
          name: contact.name,
          role: contact.role,
          phone: contact.phone,
          email: contact.email,
        }),
      ),

      customFields: values.custom,
    };

    save.mutate(outlet ? body : ({ ...body, code: values.code } satisfies CreateOutlet));
  });

  /**
   * The form's errors, rebuilt into a new object every render.
   *
   * Not a flourish — without it a server refusal reaches this component and never reaches the
   * screen. `setError` writes into the `formState.errors` object that already exists, and the React
   * Compiler (`reactCompiler: true` in next.config.ts) memoises the markup below on that object's
   * identity: the component re-renders, reads the same reference, and reuses the inputs it rendered
   * last time — no message, no `aria-invalid`. Client-side errors escape this because the resolver
   * hands back a whole new errors object each time it runs.
   *
   * Found by submitting a duplicate code against the running API. Every component test passed
   * without this line, because vitest transforms with esbuild and never runs the compiler —
   * see frontend-toolchain.md.
   */
  const errors = { ...form.formState.errors } as FieldErrors;
  const message = (name: keyof typeof form.formState.errors) =>
    errors[name]?.message as string | undefined;

  /** The props every plain input shares: registration, and the error wiring around it. */
  const bind = (name: Parameters<typeof form.register>[0]) => ({
    ...form.register(name),
    id: name,
    "aria-invalid": Boolean(errors[name]),
    "aria-describedby": errors[name] ? `${name}-error` : undefined,
    className: cn(CONTROL, errors[name] && "border-destructive"),
  });

  return (
    // noValidate: the browser's own bubbles would fire before the resolver runs and show a second,
    // differently-worded refusal for the same field. The constraints stay on the controls for the
    // keyboard they choose on a phone; the messages come from one place.
    <form onSubmit={submit} noValidate className="flex max-w-2xl flex-col gap-4">
      {/*
        A `fieldset`, because `disabled` on one propagates to every control inside it — including
        the contacts rows and the tenant's custom fields, which are built from data and would each
        have needed the flag threaded to them. `display: contents` keeps it out of the layout; the
        propagation is by DOM ancestry, not by box.

        Hiding Save alone was not enough. A reader with `outlet:read` could still type into every
        box, and a form that accepts an hour of edits and then offers nowhere to put them is a
        worse lie than one that refuses on save.

        `!isPending &&` matters, and it is the opposite rule from the Save button below.
        `usePermissions` counts a pending answer as denied, which is right for *offering* a control —
        the flash of Save arriving a moment late is harmless. It is wrong for *disabling* one: a form
        that starts editable, goes dead when the identity query resolves, and comes back is exactly
        the "appears and is then taken away" failure the hook's own comment warns about, and it eats
        whatever a fast typist put in the first box.
      */}
      <fieldset disabled={!permissions.isPending && !canWrite} className="contents">
      {refused.length > 0 ? (
        // The server's own words. It knows things this form cannot — a code taken a second ago, a
        // rule only the catalogue holds — and replacing those with "something went wrong" throws
        // away the only description of what is actually wrong.
        <ul role="alert" className="rounded-lg bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={t("code")} htmlFor="code" required={!outlet} error={message("code")}>
          <input
            {...bind("code")}
            maxLength={50}
            // Not editable after creation: it is the identifier territory memberships and import
            // files already refer to, so a rename would orphan every one of them.
            readOnly={Boolean(outlet)}
            className={cn(bind("code").className, outlet && "cursor-not-allowed text-muted-foreground")}
          />
        </Field>

        <Field label={t("name")} htmlFor="name" required error={message("name")}>
          <input {...bind("name")} maxLength={200} />
        </Field>

        <Field label={t("channel")} htmlFor="channelId" required error={message("channelId")}>
          {/* The stored channel is always an option — see `channelsIncluding`. Without it this
              select renders before the channel list arrives, cannot hold the value RHF assigns, and
              settles on whichever channel happens to be first: the form then shows an outlet as
              belonging somewhere it does not. */}
          <select {...bind("channelId")}>
            <option value="" disabled />
            {channelsIncluding(
              channels.data,
              outlet && { id: outlet.channelId, name: outlet.channelName },
            ).map((channel) => (
              <option key={channel.id} value={channel.id}>
                {channel.name}
              </option>
            ))}
          </select>
        </Field>

        <Field label={t("timeZone")} htmlFor="timeZoneId" required error={message("timeZoneId")}>
          <select {...bind("timeZoneId")}>
            <option value="" disabled />
            {zonesIncluding(outlet?.timeZoneId).map((zone) => (
              <option key={zone} value={zone}>
                {zone}
              </option>
            ))}
          </select>
        </Field>

        <Field label={t("segment")} htmlFor="segment" error={message("segment")}>
          <input {...bind("segment")} maxLength={50} />
        </Field>

        <Field label={t("banner")} htmlFor="banner" error={message("banner")}>
          <input {...bind("banner")} maxLength={100} />
        </Field>
      </div>

      <fieldset className="grid gap-4 rounded-xl border border-border p-4 sm:grid-cols-2">
        <legend className="px-1 text-xs font-semibold text-muted-foreground uppercase">
          {t("location")}
        </legend>

        <Field label={t("street")} htmlFor="street" error={message("street")}>
          <input {...bind("street")} maxLength={200} />
        </Field>

        <Field label={t("city")} htmlFor="city" error={message("city")}>
          <input {...bind("city")} maxLength={100} />
        </Field>

        <Field label={t("postalCode")} htmlFor="postalCode" error={message("postalCode")}>
          <input {...bind("postalCode")} maxLength={20} />
        </Field>

        <Field label={t("countryCode")} htmlFor="countryCode" error={message("countryCode")}>
          <input {...bind("countryCode")} maxLength={2} />
        </Field>

        <Field label={t("latitude")} htmlFor="latitude" error={message("latitude")}>
          {/* The bounds the shared GeoPoint enforces, mirrored in the schema for the message. */}
          <input {...bind("latitude")} type="number" step="any" min={-90} max={90} />
        </Field>

        <Field label={t("longitude")} htmlFor="longitude" error={message("longitude")}>
          <input {...bind("longitude")} type="number" step="any" min={-180} max={180} />
        </Field>
      </fieldset>

      <OutletContacts
        control={form.control as never}
        register={form.register as never}
        errors={errors}
      />

      <CustomFields
        definitions={definitions.data ?? []}
        control={form.control as never}
        errors={errors}
      />

      {/* Gated the way every Products screen gates its own writes. Without this a reader with
          `outlet:read` alone got a filled-in form and a Save that always 403s — and, until the
          fallback above landed, a 403 that said nothing at all. The server was refusing correctly
          throughout; what was missing was the screen admitting it. */}
      {canWrite ? (
        <div className="flex gap-2">
          <Button type="submit" disabled={save.isPending}>
            {save.isPending ? t("saving") : t("save")}
          </Button>
          <Button type="button" variant="outline" onClick={() => router.push("/outlets")}>
            {t("cancel")}
          </Button>
        </div>
      ) : (
        <p className="text-sm text-muted-foreground">{t("readOnly")}</p>
      )}
      </fieldset>
    </form>
  );
}

