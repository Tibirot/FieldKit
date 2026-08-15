// @vitest-environment jsdom

import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it } from "vitest";

import { ThemeToggle } from "@/components/theme-toggle";
import { THEME_STORAGE_KEY } from "@/lib/theme/theme";
import { render } from "@/test/render";

const option = (name: RegExp) => screen.getByRole("radio", { name });

beforeEach(() => {
  localStorage.clear();
  document.documentElement.className = "font-sans";
});

describe("<ThemeToggle>", () => {
  it("offers three states, not two", () => {
    /*
     * The decision this control exists to express. Light and dark alone would mean somebody who has
     * set their device to dark and never opens this gets light — an app overriding a preference the
     * operating system already carries, with nothing to say so and no way back.
     */
    render(<ThemeToggle />);

    expect(screen.getAllByRole("radio")).toHaveLength(3);
    expect(option(/system/i)).toBeTruthy();
  });

  it("starts on light when nobody has chosen", () => {
    render(<ThemeToggle />);

    expect(option(/light/i).getAttribute("aria-checked")).toBe("true");
    expect(option(/dark/i).getAttribute("aria-checked")).toBe("false");
  });

  it("points at what was chosen last time", () => {
    localStorage.setItem(THEME_STORAGE_KEY, "dark");

    render(<ThemeToggle />);

    expect(option(/dark/i).getAttribute("aria-checked")).toBe("true");
  });

  it("changes the document, not just itself", async () => {
    // The control that only moves its own highlight is the classic version of this bug: it looks
    // like it worked, and nothing else on the page agrees.
    render(<ThemeToggle />);

    await userEvent.click(option(/dark/i));

    expect(document.documentElement.classList.contains("dark")).toBe(true);
    expect(document.documentElement.classList.contains("light")).toBe(false);
  });

  it("takes both classes off for system, because that is how system is spelled", async () => {
    // `globals.css` reads an absent class as "obey prefers-color-scheme". Leaving `.light` on while
    // claiming to follow the device would be the one state the cascade cannot describe.
    render(<ThemeToggle />);

    await userEvent.click(option(/dark/i));
    await userEvent.click(option(/system/i));

    expect(document.documentElement.classList.contains("dark")).toBe(false);
    expect(document.documentElement.classList.contains("light")).toBe(false);
  });

  it("remembers, so the pre-paint script has something to read next time", async () => {
    // The whole point of storing it: the class this sets is gone on the next navigation, and what
    // survives is this value and the inline script that reads it before the first paint.
    render(<ThemeToggle />);

    await userEvent.click(option(/dark/i));

    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe("dark");
  });

  it("keeps the font variables it found on the document", async () => {
    // `<html>` carries them. A theme switch that assigned `className` rather than editing the list
    // would drop them and the page would fall back to a system font mid-session.
    render(<ThemeToggle />);

    await userEvent.click(option(/dark/i));

    expect(document.documentElement.classList.contains("font-sans")).toBe(true);
  });

  it("is one control with three options, not three controls", async () => {
    // A radio group is a single tab stop with arrow-key navigation. Three buttons would be three
    // stops in every keyboard traversal of a header that already has several.
    render(<ThemeToggle />);

    expect(screen.getByRole("radiogroup", { name: "Theme" })).toBeTruthy();
  });
});
