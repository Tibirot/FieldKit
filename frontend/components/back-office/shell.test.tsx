// @vitest-environment jsdom

import { screen } from "@testing-library/react";
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

function signedIn(status: AuthStatus): AuthContextValue {
  return {
    status,
    user: null,
    workspace: "fieldkit-dev",
    signIn: vi.fn(),
    signOut: vi.fn(),
    completeSignIn: vi.fn(),
  };
}

describe("<BackOfficeShell>", () => {
  beforeEach(() => {
    replace.mockClear();
  });

  it("waits rather than assuming nobody is signed in", () => {
    // The three-valued status exists for this case. Collapsing "we have not looked yet" into
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
});
