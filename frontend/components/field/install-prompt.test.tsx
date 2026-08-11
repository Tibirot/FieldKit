// @vitest-environment jsdom

import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { InstallPrompt } from "@/components/field/install-prompt";
import { render } from "@/test/render";

/**
 * Offering to install (`OFF-10`) — W9 slice 11.
 *
 * The behaviour worth pinning is all about *when not to ask*: a rep already running the installed
 * app, one who has said no, and every browser that does not fire the event at all. An install
 * banner that reappears is the kind of thing people uninstall the app over.
 */
type Choice = "accepted" | "dismissed";

/** The event Chromium fires, as much of it as the component touches. */
function installEvent(outcome: Choice) {
  const prompt = vi.fn(async () => {});
  const event = Object.assign(new Event("beforeinstallprompt"), {
    prompt,
    userChoice: Promise.resolve({ outcome }),
  });

  return { event, prompt };
}

function standalone(installed: boolean) {
  Object.defineProperty(globalThis, "matchMedia", {
    configurable: true,
    value: (query: string) => ({ matches: installed && query.includes("standalone") }),
  });
}

beforeEach(() => {
  localStorage.clear();
  standalone(false);
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe("<InstallPrompt>", () => {
  it("shows nothing until the browser says installing is possible", () => {
    // Every WebKit browser, forever: `beforeinstallprompt` is Chromium's. A button that did nothing
    // on iOS would be worse than no button, and instructions pointing at Safari's Share menu are a
    // support article rather than a feature.
    render(<InstallPrompt />);

    expect(screen.queryByText("Install FieldKit")).toBeNull();
  });

  it("offers once the browser fires the event, explaining what it buys", async () => {
    render(<InstallPrompt />);

    const { event } = installEvent("accepted");
    globalThis.dispatchEvent(event);

    expect(await screen.findByText("Install FieldKit")).toBeTruthy();

    // The reason, not the mechanism — this is the sentence that connects installing to not losing
    // a day's work, which is the only reason a rep should care.
    expect(screen.getByText(/stops clearing its data/)).toBeTruthy();
  });

  it("takes the event over so the offer lands in the app, not in browser chrome", async () => {
    render(<InstallPrompt />);

    const { event } = installEvent("accepted");
    const prevented = vi.spyOn(event, "preventDefault");

    globalThis.dispatchEvent(event);
    await screen.findByText("Install FieldKit");

    expect(prevented).toHaveBeenCalled();
  });

  it("prompts the browser when the rep accepts, and asks again on no later visit", async () => {
    render(<InstallPrompt />);

    const { event, prompt } = installEvent("accepted");
    globalThis.dispatchEvent(event);

    await userEvent.click(await screen.findByRole("button", { name: "Install" }));

    await waitFor(() => expect(prompt).toHaveBeenCalled());
    await waitFor(() => expect(screen.queryByText("Install FieldKit")).toBeNull());

    // Nothing is remembered on acceptance: `display-mode: standalone` answers next time, and a
    // stored flag would be a second copy of that fact able to disagree with it.
    expect(localStorage.getItem("fieldkit.install.dismissed")).toBeNull();
  });

  it("remembers a refusal rather than asking on every visit", async () => {
    render(<InstallPrompt />);

    globalThis.dispatchEvent(installEvent("dismissed").event);
    await userEvent.click(await screen.findByRole("button", { name: "Not now" }));

    expect(screen.queryByText("Install FieldKit")).toBeNull();
    expect(localStorage.getItem("fieldkit.install.dismissed")).toBe("1");
  });

  it("stays quiet on a later visit after a refusal", async () => {
    localStorage.setItem("fieldkit.install.dismissed", "1");

    render(<InstallPrompt />);

    /*
     * Dispatched inside `act` so React has finished with the event before the absence is asserted.
     *
     * Without it this passed with the guard **removed** — a `dispatchEvent` from outside React does
     * not flush synchronously, so `queryByText` ran before any render could have happened and found
     * nothing either way. Three of this file's tests were vacuous for exactly that reason, found by
     * sabotaging the guards and watching them all still pass.
     */
    await act(async () => {
      globalThis.dispatchEvent(installEvent("accepted").event);
    });

    // Not merely hidden — the listener is never attached, so Chromium's own infobar is left alone
    // and the rep can still install from the browser menu if they change their mind.
    expect(screen.queryByText("Install FieldKit")).toBeNull();
  });

  it("stays quiet when the app is already installed", async () => {
    standalone(true);

    render(<InstallPrompt />);

    await act(async () => {
      globalThis.dispatchEvent(installEvent("accepted").event);
    });

    expect(screen.queryByText("Install FieldKit")).toBeNull();
  });

  it("remembers a refusal made in the browser's own dialog, not just ours", async () => {
    /*
     * The gap the sabotage pass found: the *Not now* button and the browser dialog's "cancel" are
     * two different paths, and only the first had a test. Removing the `setItem` from the second
     * changed nothing, because nothing exercised it.
     */
    render(<InstallPrompt />);

    const { event } = installEvent("dismissed");
    globalThis.dispatchEvent(event);

    await userEvent.click(await screen.findByRole("button", { name: "Install" }));

    await waitFor(() => expect(localStorage.getItem("fieldkit.install.dismissed")).toBe("1"));
    expect(screen.queryByText("Install FieldKit")).toBeNull();
  });

  it("withdraws the offer when the app is installed some other way", async () => {
    // From the browser's own menu, mid-session. There is no `userChoice` to await in that case, so
    // the component has to hear about it separately or keep offering something already done.
    render(<InstallPrompt />);

    globalThis.dispatchEvent(installEvent("accepted").event);
    await screen.findByText("Install FieldKit");

    globalThis.dispatchEvent(new Event("appinstalled"));

    await waitFor(() => expect(screen.queryByText("Install FieldKit")).toBeNull());
  });
});
