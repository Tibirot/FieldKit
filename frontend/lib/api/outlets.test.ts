import { describe, expect, it } from "vitest";

import { outletsKey } from "@/lib/api/outlets";

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
