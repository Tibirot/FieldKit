"use client";

import { useTranslations } from "next-intl";
import { useEffect, useState } from "react";

import { Button } from "@/components/ui/button";

/**
 * Offering to install the app (`OFF-10`) — W9 slice 11.
 *
 * <b>Installing is not decoration on a field app; it is what makes the storage promise stick.</b>
 * `requestPersistentStorage` has been asking since W5 and browsers answer partly on how *engaged*
 * the origin is — an installed PWA is treated far more kindly than a tab, and on iOS a tab's
 * storage is cleared after seven days of not being opened. A rep who works a Friday round, does not
 * open the app over the weekend, and reconnects on Monday is exactly the case.
 *
 * <b>Only where the browser offers the event.</b> `beforeinstallprompt` is Chromium's; WebKit has
 * no equivalent and installing there is Safari's own Share ▸ Add to Home Screen, which a page
 * cannot trigger or even detect. Rather than render a button that does nothing on iOS, this shows
 * nothing there — the honest options being a working button or silence, and a set of instructions
 * pointing at a menu is a support article rather than a feature.
 */
type InstallEvent = Event & {
  prompt: () => Promise<void>;
  userChoice: Promise<{ outcome: "accepted" | "dismissed" }>;
};

/** Remembered so a rep who said no is not asked on every visit (`localStorage`, not the store). */
const DISMISSED = "fieldkit.install.dismissed";

export function InstallPrompt() {
  const t = useTranslations("Field.install");

  const [offer, setOffer] = useState<InstallEvent | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    // Already installed: the app is running from the home screen, so there is nothing to offer.
    if (globalThis.matchMedia?.("(display-mode: standalone)").matches) return;
    if (globalThis.localStorage?.getItem(DISMISSED)) return;

    const captured = (event: Event) => {
      /*
       * Chromium shows its own mini-infobar unless this is prevented, and it appears wherever it
       * likes. Taking the event means the offer lands inside the app, next to the reason it
       * matters, rather than as a browser chrome banner a rep dismisses without reading.
       */
      event.preventDefault();
      setOffer(event as InstallEvent);
    };

    globalThis.addEventListener("beforeinstallprompt", captured);

    // Fired after a successful install, including one done from the browser's own menu rather than
    // this button — in which case there is no `userChoice` to await and the offer must still go.
    const installed = () => setOffer(null);
    globalThis.addEventListener("appinstalled", installed);

    return () => {
      globalThis.removeEventListener("beforeinstallprompt", captured);
      globalThis.removeEventListener("appinstalled", installed);
    };
  }, []);

  if (!offer) return null;

  const install = async () => {
    setBusy(true);

    await offer.prompt();
    const { outcome } = await offer.userChoice;

    // The event is single-use whatever the rep chose, so the offer goes either way. A dismissal is
    // remembered; an acceptance needs no record, because `display-mode` will answer next time.
    if (outcome === "dismissed") globalThis.localStorage?.setItem(DISMISSED, "1");

    setOffer(null);
    setBusy(false);
  };

  const dismiss = () => {
    globalThis.localStorage?.setItem(DISMISSED, "1");
    setOffer(null);
  };

  return (
    <section className="flex flex-col gap-2 rounded-xl border border-border p-3">
      <div className="flex flex-col gap-1">
        <h2 className="font-medium">{t("title")}</h2>
        {/* The reason, not the mechanism. "Add to your home screen" describes what the tap does;
            what a rep needs to know is what they get for it. */}
        <p className="text-sm text-muted-foreground">{t("why")}</p>
      </div>

      <div className="flex flex-wrap gap-2">
        <Button onClick={() => void install()} disabled={busy}>
          {t("action")}
        </Button>
        <Button variant="outline" onClick={dismiss} disabled={busy}>
          {t("dismiss")}
        </Button>
      </div>
    </section>
  );
}
