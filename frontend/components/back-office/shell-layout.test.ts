import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

/**
 * The back office's scroll contract, guarded at the source.
 *
 * **The regression.** The shell was `min-h-dvh` and the *page* scrolled, while the rail and the
 * section panel were `h-dvh` — exactly one viewport tall. On the outlet list, which is 2,400px, that
 * meant the navigation ended 800px down and scrolled away: past the first screenful the left 260px
 * became empty page, and the table row sitting at that boundary read as floating on nothing. It was
 * reported as "odd scrolling behaviour, maybe the browser".
 *
 * It was not the browser. It also predates the W12½ redesign — the single sidebar had `md:h-dvh`
 * from W5 — but one narrow grey column ending is far less obvious than two columns ending, one of
 * which is the same colour as the page.
 *
 * **Why this is a source guard rather than a rendered one.** jsdom has no layout: no heights, no
 * scrollports, no `position: sticky`. A test that renders the shell cannot tell a scrolling page
 * from a scrolling column, so the only place the rule can be stated is where a person has to follow
 * it. That is the argument `globals.test.ts` already makes for its layer guard, and this is the same
 * shape of defect — one that shipped without troubling a build, a type-check or a lint run.
 *
 * The half this cannot check is that it *looks* right; that is Week 14's Playwright work, which has
 * a real engine underneath it.
 */

/**
 * The file with its comments removed.
 *
 * Load-bearing, and found the moment this was written: each of these files *explains* the defect it
 * was fixed for, so `min-h-dvh` and `h-dvh` both appear in prose. Asserting over the whole file made
 * the guard fail on its own documentation — which would have taught the next person to delete the
 * explanation rather than keep the rule.
 */
const source = (path: string) =>
  readFileSync(fileURLToPath(new URL(path, import.meta.url)), "utf8")
    .replace(/\/\*[\s\S]*?\*\//g, "")
    .replace(/^\s*\/\/.*$/gm, "");

const shell = source("./shell.tsx");
const rail = source("./sidebar.tsx");
const panel = source("./section-panel.tsx");

describe("the back-office scroll contract", () => {
  it("gives the shell exactly one viewport, so the chrome cannot outrun it", () => {
    /*
     * `min-h-dvh` lets the shell grow with its content, which is what made the page the scroller and
     * the navigation a thing that scrolled away. `h-dvh` pins it, and the column inside does the
     * scrolling instead.
     */
    expect(shell).toContain("flex h-dvh flex-col md:flex-row");
    expect(shell).not.toContain("min-h-dvh");
  });

  it("makes the content column the scroller, and lets it shrink enough to be one", () => {
    /*
     * `min-h-0` is half the fix and the half that is easy to leave out: a flex item defaults to
     * `min-height: auto` and refuses to shrink below its content, so the column would be as tall as
     * the table and `overflow-y` would have nothing to do. Same bug, one level in.
     */
    const column = shell.match(/className="([^"]*flex-1[^"]*)"[^>]*inert=\{open\}/)?.[1];

    expect(column, "the column that holds <main> and carries `inert`").toBeDefined();
    expect(column).toContain("overflow-y-auto");
    expect(column).toContain("min-h-0");
  });

  it("sizes both navigation columns to the shell rather than to the viewport", () => {
    /*
     * The actual defect. `md:h-dvh` on a column inside a taller page is a column that stops — and
     * `position: sticky` is not the escape, because `globals.css` sets `overflow-x: hidden` on
     * `html, body`, which makes `body` a scroll container that never scrolls. A sticky descendant
     * sticks to that, which is to say it does nothing.
     */
    for (const [name, file] of [
      ["the rail", rail],
      ["the section panel", panel],
    ] as const) {
      expect(file, `${name} must fill the shell`).toContain("md:h-full");
      expect(file, `${name} must not be sized to the viewport`).not.toContain("h-dvh");
    }
  });

  it("lets a navigation column scroll on its own, for the day one is taller than the screen", () => {
    // Nine sections and six screens fit today. A tenth section, or a laptop at 600px, does not —
    // and a column that overflows a fixed-height shell with no scroller of its own is unreachable.
    expect(rail).toContain("overflow-y-auto");
    expect(panel).toContain("overflow-y-auto");
  });
});
