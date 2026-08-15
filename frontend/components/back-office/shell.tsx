"use client";

import { Menu, X } from "lucide-react";
import { useTranslations } from "next-intl";
import { useEffect, useRef, useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { QueryProvider } from "@/components/back-office/query-provider";
import { SectionPanel } from "@/components/back-office/section-panel";
import { Sidebar } from "@/components/back-office/sidebar";
import { SessionGuard } from "@/components/session-guard";
import { ThemeToggle } from "@/components/theme-toggle";
import { Button } from "@/components/ui/button";
import { usePathname } from "@/i18n/navigation";
import { cn } from "@/lib/utils";

/**
 * The back office (desktop console).
 *
 * The session states — restoring, anonymous, expired — live in
 * {@link SessionGuard}, which the field app shares. What is left here is the
 * console itself: the navigation, a sign-out, and the query client the screens read through.
 */
export function BackOfficeShell({ children }: { children: React.ReactNode }) {
  return (
    <SessionGuard>
      <BackOffice>{children}</BackOffice>
    </SessionGuard>
  );
}

/** Rendered only once there is a session, so it can read one without checking. */
function BackOffice({ children }: { children: React.ReactNode }) {
  const t = useTranslations("BackOffice");
  const nav = useTranslations("Nav");
  const { workspace, signOut } = useAuth();
  const pathname = usePathname();

  /*
   * **Arriving somewhere shuts the drawer, adjusted during render rather than in an effect.**
   *
   * The obvious `useEffect(() => setOpen(false), [pathname])` is a lint error under React 19's
   * `set-state-in-effect`, and it is also a worse answer: it renders the drawer over the screen it
   * just reached, then removes it. This is React's documented pattern for state that has to reset
   * when an input changes — compare, set, and let the re-render happen before anything paints.
   *
   * **The version before this one stored the path it was opened at** and derived `open` from
   * `openedAt === pathname`, which is shorter and wrong: navigate away and back, and the two are
   * equal again, so the drawer re-opens itself on a screen nobody opened it on. What matters is the
   * *transition*, not the destination. There is a test named after that.
   *
   * A click handler on the links would have been wrong for a third reason: the rail and the panel
   * do not know they are inside a drawer, and should not have to.
   */
  const [open, setOpen] = useState(false);
  const [renderedAt, setRenderedAt] = useState(pathname);

  if (renderedAt !== pathname) {
    setRenderedAt(pathname);
    setOpen(false);
  }

  const opener = useRef<HTMLButtonElement>(null);

  /** Shut it and put the caret back where it was, so Escape does not strand a keyboard. */
  const close = () => {
    setOpen(false);
    opener.current?.focus();
  };

  // Escape closes it, because a full-screen overlay with no visible way out is a trap for anyone
  // who opened it by accident — and the close button is at the top of a list that scrolls.
  useEffect(() => {
    if (!open) return;

    const escape = (event: KeyboardEvent) => {
      if (event.key === "Escape") close();
    };

    /*
     * And the drawer is a *mobile* shape, so it has to stop being one when the viewport stops being
     * mobile. Without this, widening the window while it is open leaves the columns rendering
     * inline — correct — over content this component has marked `inert`, which is a page nobody can
     * click and nothing on screen explains. `open` survives no navigation, but a resize is not one.
     */
    const desktop = matchMedia("(min-width: 48rem)");
    const cross = () => { if (desktop.matches) setOpen(false); };

    document.addEventListener("keydown", escape);
    desktop.addEventListener("change", cross);

    return () => {
      document.removeEventListener("keydown", escape);
      desktop.removeEventListener("change", cross);
    };
  });

  return (
    <QueryProvider>
      {/*
        **A shell of exactly one viewport, with the content column as the scroller.**

        It was `min-h-dvh` with the *page* scrolling, and that left the rail and the panel — both
        `h-dvh` — ending at 800px on a 2,400px outlet table. Scroll past the first screenful and the
        navigation is simply gone: the left 260px becomes empty page, and the row at that boundary
        reads as floating, which is how this was noticed.

        `position: sticky` is the usual answer and does not work here. `globals.css` sets
        `overflow-x: hidden` on `html, body`, which makes `body` a scroll container — so a sticky
        descendant sticks to *body's* scrollport, and body never scrolls. Fixing that would mean
        unpicking a rule that exists to stop horizontal overflow.

        So the chrome is a fixed-height row and the content scrolls inside it, which is what a
        console with a permanent rail wants anyway: the navigation is reachable from the bottom of a
        long table without scrolling back up to find it.
      */}
      <div className="flex h-dvh flex-col md:flex-row">
        {/*
          The mobile bar, and the only thing on this screen that is *not* also on the desktop one.
          The back office is desktop-first (ADR-0004), so this is the "works on a phone" bar rather
          than a second design — but 68px of rail beside 192px of panel is 260px of a 375px screen,
          so the two columns cannot simply stay where they are.
        */}
        <header className="flex items-center gap-3 border-b border-border bg-muted/40 px-4 py-2 md:hidden">
          <Button
            ref={opener}
            variant="outline"
            size="sm"
            aria-expanded={open}
            aria-controls="back-office-navigation"
            onClick={() => (open ? close() : setOpen(true))}
          >
            {open ? <X className="size-4" /> : <Menu className="size-4" />}
            {nav(open ? "closeMenu" : "openMenu")}
          </Button>

          <span className="min-w-0 truncate font-mono text-[11px] text-muted-foreground">
            {workspace ?? nav("noWorkspace")}
          </span>
        </header>

        {/*
          One instance, not one per breakpoint. Rendering the rail and the panel twice — once inline,
          once inside a drawer — would put two `Back office` landmarks in the document, and a screen
          reader listing them cannot tell which of the two is the hidden one.

          So the columns are always the same columns, and only their container moves: a static flex
          row from `md` up, a full-height overlay below it, and `hidden` below it when shut.
        */}
        <div
          id="back-office-navigation"
          className={cn(
            "md:flex",
            // `max-md:` on every overlay class, so the drawer is a shape this layout takes below the
            // breakpoint and never above it — from `md` up these are the same two static columns
            // whether or not somebody left `openedAt` set.
            open
              ? "flex max-md:fixed max-md:inset-0 max-md:z-40 max-md:overflow-y-auto max-md:bg-background"
              : "hidden",
          )}
        >
          <Sidebar workspace={workspace} />
          <SectionPanel />
        </div>

        {/*
          Below the drawer rather than beside it, so a tap that misses the list lands on the page
          and not on nothing. `aria-hidden` because it is a backdrop: the drawer above it is the
          content, and announcing an unlabelled button here would be one more thing to skip past.
        */}
        {open ? (
          <button
            type="button"
            aria-hidden="true"
            tabIndex={-1}
            className="fixed inset-0 z-30 bg-foreground/20 md:hidden"
            onClick={close}
          />
        ) : null}

        {/*
          `inert` while the drawer is over it, and this is the half a backdrop does not do. A
          full-screen overlay hides the page from the eye and not from the tab key: without this,
          tabbing past the last screen in the panel walks into the sign-out button and the table
          behind, both invisible. The backdrop stops a mouse; only this stops a keyboard.
        */}
        {/*
          The scroller. `min-h-0` because a flex item's default `min-height: auto` refuses to shrink
          below its content — without it this column is as tall as the table and `overflow-y` never
          has anything to do, which is the same bug one level in.
        */}
        <div className="flex min-h-0 min-w-0 flex-1 flex-col overflow-y-auto" inert={open}>
          <header className="flex items-center justify-end gap-3 border-b border-border px-4 py-3 md:px-6">
            <ThemeToggle />
            <Button variant="outline" size="sm" onClick={() => void signOut()}>
              {t("signOut")}
            </Button>
          </header>
          <main className="min-w-0 flex-1 p-4 md:p-6">{children}</main>
        </div>
      </div>
    </QueryProvider>
  );
}
