"use client";

import { Monitor, Moon, Sun } from "lucide-react";
import { useTranslations } from "next-intl";
import { useSyncExternalStore } from "react";

import {
  chooseTheme,
  serverTheme,
  storedTheme,
  subscribeToTheme,
  type Theme,
  THEMES,
} from "@/lib/theme/theme";
import { cn } from "@/lib/utils";

/**
 * Choosing the palette (W12½ slice 7, `A7`).
 *
 * **Three states, and the third is the point.** Light and dark alone would mean somebody who has set
 * their device to dark and never opens this control gets light — an app overriding a preference the
 * operating system already carries, with no way to say so. `System` is what makes light-by-default a
 * default rather than an imposition.
 *
 * **A radio group, not a switch.** Three mutually exclusive choices is what a radio group is for, and
 * it gets arrow-key navigation and a single tab stop for free. A three-position switch would be a
 * control nobody has seen before, and cycling through with one button hides two of the three states
 * from anyone who cannot see the current one.
 */

const ICONS: Record<Theme, React.ComponentType<{ className?: string }>> = {
  light: Sun,
  dark: Moon,
  system: Monitor,
};

export function ThemeToggle({ className }: { className?: string }) {
  const t = useTranslations("Theme");

  /*
   * **Read through a store, because the server cannot know the answer and should not pretend to.**
   *
   * The choice lives in `localStorage`. A server render has none, so it shows the default while the
   * browser shows whatever was chosen — and that is a real disagreement rather than a mistake.
   * `useSyncExternalStore` is the hook that models it: a server snapshot, a client snapshot, and a
   * re-render between them.
   *
   * This was `useState(storedTheme)` first, which hydrated with `aria-checked` on the wrong option
   * and said so in the console. Suppressing that warning would have hidden a disagreement rather
   * than described one — and the wrong option is not cosmetic here, since `aria-checked` is the
   * whole of what a screen reader is told.
   */
  const theme = useSyncExternalStore(subscribeToTheme, storedTheme, serverTheme);

  const choose = (next: Theme) => chooseTheme(next, document.documentElement);

  return (
    <div
      role="radiogroup"
      aria-label={t("label")}
      className={cn(
        "inline-flex items-center gap-0.5 rounded-lg border border-border bg-muted/40 p-0.5",
        className,
      )}
    >
      {THEMES.map((option) => {
        const Icon = ICONS[option];
        const active = option === theme;

        return (
          <button
            key={option}
            type="button"
            role="radio"
            aria-checked={active}
            // The label is the accessible name; the icon alone would leave the control unreadable to
            // anyone who cannot see it, and unguessable to anyone who can but has not met it before.
            aria-label={t(option)}
            title={t(option)}
            className={cn(
              "grid size-7 place-items-center rounded-md transition-colors",
              active
                ? "bg-background text-foreground shadow-sm"
                : "text-muted-foreground hover:text-foreground",
            )}
            onClick={() => choose(option)}
          >
            <Icon className="size-4" />
          </button>
        );
      })}
    </div>
  );
}
