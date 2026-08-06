"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import { useForm, type FieldErrors } from "react-hook-form";
import { z } from "zod";

import { useAuth } from "@/components/auth-provider";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import { createChannel, updateChannel, type Channel } from "@/lib/api/channels";
import { useValidationMessages } from "@/lib/forms/use-validation-messages";
import { cn } from "@/lib/utils";

const CONTROL =
  "h-9 w-full rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/**
 * Name a trade classification (`OUT-01`).
 *
 * One field, and the reason it is still a form rather than an inline input: the name is unique per
 * tenant and the server says so by refusing, which has to land under the box. `Modern Trade` and
 * `modern trade` are the same channel — the index enforces that over `lower(name)` — so a rename
 * that collides is the ordinary case rather than the odd one.
 */
export function ChannelForm({
  channel,
  onDone,
  onCancel,
}: {
  channel?: Channel;
  onDone: () => void;
  onCancel: () => void;
}) {
  const t = useTranslations("Channels");
  const messages = useValidationMessages();
  const { user } = useAuth();
  const client = useQueryClient();

  const accessToken = user?.access_token;

  const form = useForm({
    resolver: zodResolver(
      z.object({
        name: z.string().trim().min(1, { message: messages.required }).max(100, {
          message: messages.tooLong(100),
        }),
      }),
    ),
    mode: "onBlur",
    defaultValues: { name: channel?.name ?? "" },
  });

  const [refused, setRefused] = useState<readonly string[]>([]);

  const save = useMutation({
    mutationFn: (values: { name: string }) =>
      channel ? updateChannel(accessToken!, channel.id, values) : createChannel(accessToken!, values),

    onSuccess: async () => {
      // Outlets too: the list shows a channel name per row and the filter is built from this list,
      // so a rename changes rows on a screen this form does not own.
      await client.invalidateQueries({ queryKey: ["channels"] });
      await client.invalidateQueries({ queryKey: ["outlets"] });
      onDone();
    },

    onError: (error) => {
      if (!(error instanceof ApiError)) {
        setRefused([t("saveFailed")]);
        return;
      }

      const unattributed: string[] = [];

      for (const problem of error.problems) {
        if (problem.field === "name") {
          form.setError("name", { type: "server", message: problem.message });
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

  return (
    <form
      onSubmit={form.handleSubmit((values) => {
        setRefused([]);
        save.mutate(values);
      })}
      noValidate
      className="flex flex-col gap-4 rounded-xl border border-border p-4"
    >
      <h2 className="text-sm font-semibold">{channel ? t("editTitle") : t("newTitle")}</h2>

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-lg bg-destructive/10 px-3 py-2 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      <div className="flex max-w-md flex-col gap-1.5">
        <label htmlFor="channelName" className="text-sm font-medium">
          {t("name")}
        </label>
        <input
          {...form.register("name")}
          id="channelName"
          maxLength={100}
          aria-invalid={Boolean(errors.name)}
          aria-describedby={errors.name ? "channelName-error" : undefined}
          className={cn(CONTROL, errors.name && "border-destructive")}
        />
        {errors.name ? (
          <p id="channelName-error" className="text-xs text-destructive">
            {errors.name.message as string}
          </p>
        ) : null}
        <p className="text-xs text-muted-foreground">{t("nameHint")}</p>
      </div>

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
