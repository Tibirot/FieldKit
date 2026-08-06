"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useTranslations } from "next-intl";
import { useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { ChannelForm } from "@/components/back-office/channel-form";
import { Button } from "@/components/ui/button";
import { channelsKey, deleteChannel, fetchChannels, type Channel } from "@/lib/api/channels";
import { ApiError } from "@/lib/api/client";
import { usePermissions } from "@/lib/auth/use-permissions";

/**
 * The trade classifications a tenant works with (`OUT-01`).
 *
 * **Every outlet has one** (`BR-OUT-1`), and a channel is what drives assortment, pricing and the
 * visit workflow — so this is not decoration, it is the first thing a workspace needs. Nothing here
 * existed until the Phase 1 demo tried to create an outlet in a tenant with no channels and found
 * the dropdown empty with no way to fill it.
 *
 * **Its own route rather than a section on the outlet list.** Channels are set up once during
 * onboarding and rarely touched again, while the outlet list is a daily screen — the same reasoning
 * that put the import behind `/outlets/import` rather than in the header.
 */
export function ChannelBrowser() {
  const t = useTranslations("Channels");
  const { user } = useAuth();
  const client = useQueryClient();
  const { has } = usePermissions();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;

  const channels = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey: channelsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchChannels(accessToken!, signal),
  });

  const [editing, setEditing] = useState<Channel | "new" | null>(null);
  const [refused, setRefused] = useState<readonly string[]>([]);

  const remove = useMutation({
    mutationFn: (channel: Channel) => deleteChannel(accessToken!, channel.id),
    onSuccess: async () => {
      setRefused([]);
      await client.invalidateQueries({ queryKey: ["channels"] });
    },
    onError: (error) => {
      // "12 outlet(s) are classified as 'Modern Trade'. Reclassify them first." names the count and
      // the next step; "could not delete" names neither.
      setRefused(
        error instanceof ApiError && error.problems.length > 0
          ? error.problems.map((problem) => problem.message)
          : [t("deleteFailed")],
      );
    },
  });

  const rows = channels.data ?? [];

  return (
    <div className="flex flex-col gap-4">
      {has("channel:write") ? (
        <div>
          <Button type="button" size="sm" onClick={() => setEditing("new")}>
            <Plus className="size-4" />
            {t("newChannel")}
          </Button>
        </div>
      ) : null}

      {editing !== null ? (
        <ChannelForm
          // Remounted per target: react-hook-form captures its defaults on the first render.
          key={editing === "new" ? "new" : editing.id}
          channel={editing === "new" ? undefined : editing}
          onDone={() => setEditing(null)}
          onCancel={() => setEditing(null)}
        />
      ) : null}

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-xl bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      {channels.isPending ? (
        <p className="text-sm text-muted-foreground">{t("loading")}</p>
      ) : channels.isError ? (
        <p role="alert" className="text-sm text-destructive">
          {channels.error instanceof ApiError && channels.error.status === 403
            ? t("forbidden")
            : t("failed")}
        </p>
      ) : rows.length === 0 ? (
        // The state that made this screen necessary: no channels means no outlets, and until now
        // nothing said so or offered a way out.
        <p className="text-sm text-muted-foreground">{t("empty")}</p>
      ) : (
        <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
          {rows.map((channel) => (
            <li key={channel.id} className="flex flex-wrap items-center gap-3 px-4 py-2 text-sm">
              <span className="font-medium">{channel.name}</span>

              <div className="ml-auto flex gap-2">
                {has("channel:write") ? (
                  <>
                    <Button
                      type="button"
                      size="sm"
                      variant="outline"
                      onClick={() => setEditing(channel)}
                      aria-label={t("editNamed", { name: channel.name })}
                    >
                      {t("edit")}
                    </Button>
                    <Button
                      type="button"
                      size="sm"
                      variant="outline"
                      disabled={remove.isPending}
                      onClick={() => remove.mutate(channel)}
                      aria-label={t("deleteNamed", { name: channel.name })}
                    >
                      {t("delete")}
                    </Button>
                  </>
                ) : null}
              </div>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
