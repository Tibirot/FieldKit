"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import { useForm, type FieldErrors, type FieldPath as RhfFieldPath } from "react-hook-form";
import { z } from "zod";

import { useAuth } from "@/components/auth-provider";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import { createUser, updateUser, type Role, type User, type UserWrite } from "@/lib/api/users";
import { useValidationMessages } from "@/lib/forms/use-validation-messages";
import { zonesIncluding } from "@/lib/time-zones";
import { cn } from "@/lib/utils";

const CONTROL =
  "h-9 w-full rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

type Values = {
  subjectId: string;
  email: string;
  displayName: string;
  locale: string;
  timeZone: string;
  roleIds: string[];
};

type FieldPath = RhfFieldPath<Values>;

/**
 * Tags worth suggesting, not the set of allowed ones.
 *
 * A `<datalist>` offers without restricting, which is the only honest control here: the server
 * validates against the runtime's culture database and there is no browser API that enumerates it.
 * Time zones have `Intl.supportedValuesOf` and are closer, but not exhaustive either — see
 * `zonesIncluding` for the record that proved it.
 */
const SUGGESTED_LOCALES = ["ro-RO", "en-GB", "en-US", "de-DE", "fr-FR", "es-ES", "pl-PL", "hu-HU"];

/**
 * Roughly BCP-47 — a language, optionally a script and a region.
 *
 * Deliberately shallow, and the server is the authority: it asks ICU whether the tag is a
 * <i>predefined</i> culture, which is a question this pattern cannot ask. What it catches is a name
 * typed into the box instead of a tag.
 */
const LOCALE_TAG = /^[a-z]{2,3}(-[A-Za-z]{4})?(-([A-Z]{2}|\d{3}))?$/;

/**
 * A user profile and the roles it holds (`IAM-03`).
 *
 * **The subject id is typed in, and that is temporary.** It is the Keycloak `sub`, which today an
 * admin copies from the realm — until tenant provisioning (`IAM-10`) creates accounts, there is
 * nowhere for this screen to get it from. Named and explained on the form rather than hidden behind
 * a generated value that would not match a real account.
 *
 * **Locale is the app's own list, not every BCP-47 tag.** The server accepts anything the runtime
 * knows, but a locale this UI cannot render is a profile that produces an untranslated screen for
 * whoever holds it.
 */
export function UserForm({
  user,
  roles,
  onDone,
  onCancel,
}: {
  user?: User;
  roles: Role[];
  onDone: () => void;
  onCancel: () => void;
}) {
  const t = useTranslations("Users");
  const messages = useValidationMessages();
  const { user: signedIn } = useAuth();
  const client = useQueryClient();

  const accessToken = signedIn?.access_token;

  const form = useForm({
    resolver: zodResolver(
      z.object({
        subjectId: z.string().trim().min(1, { message: messages.required }),
        email: z
          .string()
          .trim()
          .min(1, { message: messages.required })
          .refine((value) => /^[^\s@]+@[^\s@]+$/.test(value), { message: messages.notAnEmail }),
        displayName: z.string().trim().min(1, { message: messages.required }).max(200, {
          message: messages.tooLong(200),
        }),
        locale: z
          .string()
          .trim()
          .min(1, { message: messages.required })
          .refine((value) => LOCALE_TAG.test(value), { message: t("localeShape") }),
        timeZone: z.string().min(1, { message: messages.required }),

        // BR-IAM-3, checked here so the message lands on the control rather than arriving as a
        // refusal after a round trip. The server enforces it too — this is a courtesy, not the rule.
        roleIds: z.array(z.string()).min(1, { message: t("needsARole") }),
      }),
    ),
    mode: "onBlur",
    defaultValues: {
      subjectId: user?.subjectId ?? "",
      email: user?.email ?? "",
      displayName: user?.displayName ?? "",
      // Whatever the user already has, untouched. An earlier version offered the app's two UI
      // locales as a select — which cannot express `ro-RO`, so opening an existing user showed an
      // empty box and saving would have quietly changed their formatting locale to something else.
      // A user's locale drives formatting, not only translation (ADR-0010, BR-IAM-5); the UI's own
      // language list is a different set that happens to overlap.
      locale: user?.locale ?? "",
      timeZone: user?.timeZone ?? "",
      roleIds: user?.roleIds ?? [],
    },
  });

  const [refused, setRefused] = useState<readonly string[]>([]);

  const save = useMutation({
    mutationFn: (values: UserWrite) =>
      user ? updateUser(accessToken!, user.id, values) : createUser(accessToken!, values),

    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ["users"] });
      onDone();
    },

    onError: (error) => {
      if (!(error instanceof ApiError)) {
        setRefused([t("saveFailed")]);
        return;
      }

      const unattributed: string[] = [];

      for (const problem of error.problems) {
        if (problem.field && problem.field in form.getValues()) {
          form.setError(problem.field as FieldPath, { type: "server", message: problem.message });
        } else {
          unattributed.push(problem.message);
        }
      }

      setRefused(unattributed);
    },
  });

  // Rebuilt every render: react-hook-form writes into the errors object it already has, and the
  // React Compiler memoises this markup on that object's identity (frontend-toolchain.md).
  const errors = { ...form.formState.errors } as FieldErrors;
  const message = (name: keyof Values) => errors[name]?.message as string | undefined;

  function bind(name: Exclude<keyof Values, "roleIds">) {
    return {
      ...form.register(name),
      id: name,
      "aria-invalid": Boolean(errors[name]),
      "aria-describedby": errors[name] ? `${name}-error` : undefined,
      className: cn(CONTROL, errors[name] && "border-destructive"),
    };
  }

  /**
   * The message under a control, or nothing.
   *
   * A function that returns markup, not a component — a component declared inside render is a new
   * type on every render, so React unmounts and remounts its subtree each time. The compiler's lint
   * refuses it, and it is right to: the same shape as an `<input>` losing focus mid-typing.
   */
  function errorFor(name: keyof Values) {
    return message(name) ? (
      <p id={`${name}-error`} className="text-xs text-destructive">
        {message(name)}
      </p>
    ) : null;
  }

  return (
    <form
      onSubmit={form.handleSubmit((values) => {
        setRefused([]);
        save.mutate(values);
      })}
      noValidate
      className="flex flex-col gap-4 rounded-xl border border-border p-4"
    >
      <h2 className="text-sm font-semibold">{user ? t("editTitle") : t("newTitle")}</h2>

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-lg bg-destructive/10 px-3 py-2 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      <div className="grid gap-4 sm:grid-cols-2">
        <div className="flex flex-col gap-1.5 sm:col-span-2">
          <label htmlFor="subjectId" className="text-sm font-medium">
            {t("subjectId")}
          </label>
          <input
            {...bind("subjectId")}
            // Never editable after creation: it is what every other module refers to this person by
            // — a rep assignment among them — so changing it would orphan their work rather than
            // move it.
            readOnly={Boolean(user)}
            className={cn(bind("subjectId").className, user && "cursor-not-allowed text-muted-foreground")}
          />
          <p className="text-xs text-muted-foreground">{t("subjectIdHint")}</p>
          {errorFor("subjectId")}
        </div>

        <div className="flex flex-col gap-1.5">
          <label htmlFor="displayName" className="text-sm font-medium">
            {t("displayName")}
          </label>
          <input {...bind("displayName")} maxLength={200} />
          {errorFor("displayName")}
        </div>

        <div className="flex flex-col gap-1.5">
          <label htmlFor="email" className="text-sm font-medium">
            {t("email")}
          </label>
          <input {...bind("email")} type="email" />
          {errorFor("email")}
        </div>

        <div className="flex flex-col gap-1.5">
          <label htmlFor="locale" className="text-sm font-medium">
            {t("locale")}
          </label>
          <input {...bind("locale")} list="locale-suggestions" />
          <datalist id="locale-suggestions">
            {SUGGESTED_LOCALES.map((locale) => (
              <option key={locale} value={locale} />
            ))}
          </datalist>
          <p className="text-xs text-muted-foreground">{t("localeHint")}</p>
          {errorFor("locale")}
        </div>

        <div className="flex flex-col gap-1.5">
          <label htmlFor="timeZone" className="text-sm font-medium">
            {t("timeZone")}
          </label>
          <select {...bind("timeZone")}>
            <option value="" disabled />
            {zonesIncluding(user?.timeZone).map((zone) => (
              <option key={zone} value={zone}>
                {zone}
              </option>
            ))}
          </select>
          {errorFor("timeZone")}
        </div>
      </div>

      <fieldset
        className="flex flex-col gap-2"
        aria-invalid={Boolean(errors.roleIds)}
        aria-describedby={errors.roleIds ? "roleIds-error" : undefined}
      >
        <legend className="text-sm font-medium">{t("roles")}</legend>

        {/*
          Checkboxes rather than a multi-select: a role is a bundle of permissions someone is
          deciding about one at a time, and a list where ctrl-click silently drops the other
          selections is the wrong control for a decision that BR-IAM-3 says cannot end up empty.
        */}
        {roles.map((role) => (
          <label key={role.id} className="flex items-center gap-2 text-sm">
            <input
              type="checkbox"
              value={role.id}
              {...form.register("roleIds")}
              className="size-4 accent-primary"
            />
            {role.name}
            {role.isSystemTemplate ? (
              <span className="rounded-full bg-muted px-2 py-0.5 text-xs text-muted-foreground">
                {t("systemRole")}
              </span>
            ) : null}
          </label>
        ))}

        {errorFor("roleIds")}
      </fieldset>

      <div className="flex gap-2">
        <Button type="submit" size="sm" disabled={save.isPending}>
          {save.isPending ? t("saving") : t("save")}
        </Button>
        <Button type="button" size="sm" variant="outline" onClick={onCancel}>
          {t("cancel")}
        </Button>
      </div>
    </form>
  );
}
