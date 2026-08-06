// @vitest-environment jsdom

import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue, AuthStatus } from "@/components/auth-provider";
import { BackOfficeShell } from "@/components/back-office/shell";
import { render } from "@/test/render";

const replace = vi.fn();

vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children }: { href: string; children: React.ReactNode }) => (
    <a href={href}>{children}</a>
  ),
  usePathname: () => "/outlets",
  useRouter: () => ({ replace }),
}));

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

const reauthenticate = vi.fn();
const signOut = vi.fn();

function signedIn(status: AuthStatus): AuthContextValue {
  return {
    status,
    user: null,
    workspace: "fieldkit-dev",
    signIn: vi.fn(),
    signOut,
    completeSignIn: vi.fn(),
    expire: vi.fn(),
    reauthenticate,
  };
}

describe("<BackOfficeShell>", () => {
  beforeEach(() => {
    replace.mockClear();
    signOut.mockClear();
    reauthenticate.mockReset().mockResolvedValue(true);
  });

  it("waits rather than assuming nobody is signed in", () => {
    // The four-valued status exists for this case. Collapsing "we have not looked yet" into
    // "anonymous" flashes the sign-in screen at an already signed-in user on every reload — which,
    // on a phone, reads as having been logged out.
    auth.current = signedIn("loading");

    render(
      <BackOfficeShell>
        <p>outlet table</p>
      </BackOfficeShell>,
    );

    expect(screen.getByRole("status").textContent).toContain("Restoring");
    expect(screen.queryByText("outlet table")).toBeNull();
    expect(replace).not.toHaveBeenCalled();
  });

  it("sends an anonymous visitor to sign in, and shows them nothing on the way", () => {
    auth.current = signedIn("anonymous");

    render(
      <BackOfficeShell>
        <p>outlet table</p>
      </BackOfficeShell>,
    );

    expect(replace).toHaveBeenCalledWith("/login");
    expect(screen.queryByText("outlet table")).toBeNull();
  });

  it("renders the back office once there is a session", () => {
    auth.current = signedIn("authenticated");

    render(
      <BackOfficeShell>
        <p>outlet table</p>
      </BackOfficeShell>,
    );

    expect(screen.getByText("outlet table")).toBeTruthy();
    expect(screen.getByRole("navigation", { name: "Back office" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Sign out" })).toBeTruthy();
    expect(replace).not.toHaveBeenCalled();
  });

  it("asks an expired session to prove itself instead of showing an empty back office", () => {
    // The behaviour this replaces. A dead token left `status` at "authenticated", so the shell
    // rendered — and then `/api/auth/whoami` answered 401, `usePermissions` fell back to an empty
    // set, and every gated nav item and button disappeared. The app looked like it had decided this
    // person may do nothing, rather than that it no longer knew who they were.
    auth.current = signedIn("expired");

    render(
      <BackOfficeShell>
        <p>outlet table</p>
      </BackOfficeShell>,
    );

    expect(screen.getByRole("alert").textContent).toContain("Your session has expired");
    expect(screen.getByRole("button", { name: "Sign in again" })).toBeTruthy();

    // Not the back office, and not a redirect either: the page is a question, so it waits for an
    // answer rather than navigating out from under someone mid-task.
    expect(screen.queryByText("outlet table")).toBeNull();
    expect(replace).not.toHaveBeenCalled();
  });

  it("signs back in to the workspace already on the device, without asking for it again", async () => {
    auth.current = signedIn("expired");

    render(
      <BackOfficeShell>
        <p>outlet table</p>
      </BackOfficeShell>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Sign in again" }));

    expect(reauthenticate).toHaveBeenCalled();
    expect(replace).not.toHaveBeenCalled();
  });

  it("falls back to the sign-in screen when there is nothing left to sign back in with", async () => {
    // `reauthenticate` returns false when the workspace or the stored Keycloak address is gone or
    // unusable. Without this branch the button would do nothing at all — the worst outcome, since
    // the prompt has just promised it will.
    reauthenticate.mockResolvedValue(false);
    auth.current = signedIn("expired");

    render(
      <BackOfficeShell>
        <p>outlet table</p>
      </BackOfficeShell>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Sign in again" }));

    await waitFor(() => expect(replace).toHaveBeenCalledWith("/login"));
  });

  it("still lets an expired session sign out rather than back in", async () => {
    // Signing back in is the likely intent, not the only one — someone at a shared machine wants
    // out. An expired session that can only re-authenticate is a trap.
    auth.current = signedIn("expired");

    render(
      <BackOfficeShell>
        <p>outlet table</p>
      </BackOfficeShell>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Sign out" }));

    expect(signOut).toHaveBeenCalled();
    expect(reauthenticate).not.toHaveBeenCalled();
  });
});
