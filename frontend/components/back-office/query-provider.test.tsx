// @vitest-environment jsdom

import { useQuery } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AuthContextValue } from "@/components/auth-provider";
import { QueryProvider } from "@/components/back-office/query-provider";
import { ApiError } from "@/lib/api/client";

const auth = vi.hoisted(() => ({ current: {} as AuthContextValue }));

vi.mock("@/components/auth-provider", () => ({ useAuth: () => auth.current }));

const expire = vi.fn();

/** A component whose only job is to fail the way the API fails. */
function Failing({ error }: { error: unknown }) {
  const query = useQuery({
    queryKey: ["failing", String(error)],
    queryFn: () => Promise.reject(error),
    retry: false,
  });

  return <p>{query.isError ? "failed" : "pending"}</p>;
}

function renderFailing(error: unknown) {
  return render(
    <QueryProvider>
      <Failing error={error} />
    </QueryProvider>,
  );
}

describe("<QueryProvider>", () => {
  beforeEach(() => {
    expire.mockClear();
    auth.current = { expire } as unknown as AuthContextValue;
  });

  it("treats a 401 from anywhere as the session being over", async () => {
    // The whole point of handling this centrally. A 401 can come from any query on any screen and
    // means the same thing every time, and before this nothing was listening: `usePermissions` just
    // fell back to an empty permission set and the UI hid everything it gates.
    renderFailing(new ApiError(401));

    await waitFor(() => expect(screen.getByText("failed")).toBeTruthy());
    expect(expire).toHaveBeenCalled();
  });

  it("leaves a 403 alone, because that is an answer rather than a question", async () => {
    // 403 is the API saying this caller may not do this — with a perfectly good token. Ending the
    // session over one would log people out for clicking something they lack a permission for,
    // which is both wrong and impossible to tell apart from a real expiry.
    renderFailing(new ApiError(403));

    await waitFor(() => expect(screen.getByText("failed")).toBeTruthy());
    expect(expire).not.toHaveBeenCalled();
  });

  it("leaves a network failure alone", async () => {
    // A rep in a stockroom is not signed out. Unreachable is not rejected, and conflating them is
    // exactly the behaviour the offline story cannot have.
    renderFailing(new TypeError("Failed to fetch"));

    await waitFor(() => expect(screen.getByText("failed")).toBeTruthy());
    expect(expire).not.toHaveBeenCalled();
  });
});
