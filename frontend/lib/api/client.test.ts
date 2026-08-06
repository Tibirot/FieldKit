import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiError, apiDelete, apiGet, apiSend } from "@/lib/api/client";

/** A response as `fetch` would hand it back. */
function reply(status: number, body?: string) {
  return new Response(body ?? null, {
    status,
    headers: body ? { "Content-Type": "application/json" } : undefined,
  });
}

afterEach(() => vi.unstubAllGlobals());

describe("apiSend", () => {
  it("reads what a create answered with", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(reply(201, '{"id":"019f-1"}')));

    await expect(apiSend("POST", "/api/things", "token", {})).resolves.toEqual({ id: "019f-1" });
  });

  it("treats a 204 as success with nothing to read", async () => {
    // Some POSTs answer 204 — assigning outlets to a territory is one. Parsing a body that is empty
    // by definition throws, and it surfaces as a failed mutation for a write that happened, which is
    // the worst shape of error: a retry then does the thing twice.
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(reply(204)));

    await expect(apiSend("POST", "/api/things", "token", {})).resolves.toBeUndefined();
  });

  it("keeps the status and the problems from a refusal", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(reply(409, '{"errors":[{"field":"outletIds","message":"Taken."}]}')),
    );

    const error = await apiSend("POST", "/api/things", "token", {}).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(409);
    expect((error as ApiError).problems).toEqual([{ field: "outletIds", message: "Taken." }]);
  });

  it("keeps the status when the refusal has no readable body", async () => {
    // A 403 has nothing to add — the status is the part that must not be lost.
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(reply(403)));

    const error = await apiGet("/api/things", "token").catch((e: unknown) => e);

    expect((error as ApiError).status).toBe(403);
    expect((error as ApiError).problems).toEqual([]);
  });
});

describe("apiDelete", () => {
  it("returns nothing on success and still refuses loudly", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(reply(204)));
    await expect(apiDelete("/api/things/1", "token")).resolves.toBeUndefined();

    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(reply(409, '{"errors":[{"field":null,"message":"Still in use."}]}')),
    );

    const error = await apiDelete("/api/things/1", "token").catch((e: unknown) => e);

    expect((error as ApiError).problems[0].message).toBe("Still in use.");
  });
});
