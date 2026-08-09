import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { fetchOutlets, outletsKey } from "@/lib/api/outlets";

describe("outlet cache key", () => {
  it("separates one signed-in subject from the next", () => {
    // The query cache outlives a sign-out — it lives in a QueryClient held by a React tree that
    // does not unmount when someone signs out on a shared browser. A bare ["outlets"] key would
    // hand the previous tenant's rows to the next person to sign in, from cache, before any request
    // is made. Keying by subject makes that impossible rather than something a sign-out handler has
    // to remember.
    expect(outletsKey("subject-a")).not.toEqual(outletsKey("subject-b"));
  });

  it("is stable for the same subject, so a re-render reuses the cache", () => {
    expect(outletsKey("subject-a")).toEqual(outletsKey("subject-a"));
  });
});

describe("outlet query", () => {
  const captured: string[] = [];

  beforeEach(() => {
    captured.length = 0;

    vi.stubGlobal("fetch", (url: string) => {
      captured.push(url);
      return Promise.resolve(
        new Response(JSON.stringify({ items: [], total: 0, page: 1, pageSize: 50 }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        }),
      );
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("sends only what was asked for", async () => {
    // An empty search box must not become `search=`. The server escapes and matches whatever it is
    // given, so an empty pattern is a filter that narrows nothing while making the query harder to
    // plan — and it makes the URL, which the screen keeps in sync with, misrepresent the state.
    await fetchOutlets("token", { page: 2, search: "" });

    expect(captured[0]).toBe("/api/outlets?page=2");
  });

  it("asks for nothing at all when there is nothing to ask", async () => {
    await fetchOutlets("token", {});

    expect(captured[0]).toBe("/api/outlets");
  });

  it("carries every filter it was given", async () => {
    await fetchOutlets("token", {
      search: "cluj",
      status: "Closed",
      sort: "Name",
      descending: true,
      page: 3,
    });

    const query = new URLSearchParams(captured[0].split("?")[1]);

    expect(query.get("search")).toBe("cluj");
    expect(query.get("status")).toBe("Closed");
    expect(query.get("sort")).toBe("Name");
    expect(query.get("descending")).toBe("true");
    expect(query.get("page")).toBe("3");
  });

  it("asks about a set of shops, and about none of them, differently", async () => {
    // The one parameter here that is sent when empty. `ids` is a filter with three states — absent
    // is "no filter", a list is "these", and an empty list is "none of them" — and the server can
    // only tell them apart if the empty case is sent. Omitting it would ask for the entire outlet
    // base on behalf of a caller that wanted nothing, which is the failure mode a journey plan with
    // no calls would have hit.
    await fetchOutlets("token", { ids: ["a", "b"] });
    await fetchOutlets("token", { ids: [] });
    await fetchOutlets("token", { page: 1 });

    expect(new URLSearchParams(captured[0].split("?")[1]).get("ids")).toBe("a,b");
    expect(new URLSearchParams(captured[1].split("?")[1]).get("ids")).toBe("");
    expect(new URLSearchParams(captured[2].split("?")[1]).get("ids")).toBeNull();
  });

  it("keys the cache by the query, so page 2 is not page 1's cache entry", () => {
    // Without this, paging overwrites one entry: going back to page 1 refetches, and a slow
    // response for page 2 can land after page 3 and render the wrong rows under the right pager.
    expect(outletsKey("subject-a", { page: 1 })).not.toEqual(outletsKey("subject-a", { page: 2 }));
  });
});
