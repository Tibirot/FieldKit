import { describe, expect, it } from "vitest";

import { duplicateKeys, isChoice } from "@/lib/api/surveys";

/**
 * The two pieces of survey logic that are not markup (`AUD-04`) — W10 slice 9a.
 *
 * `duplicateKeys` exists because the editor *derives* keys from question text, so a collision is
 * something the screen causes rather than something an admin types. The server refuses it by name
 * without saying which two questions collided.
 */
describe("duplicate question keys", () => {
  it("names a key two questions derived alike", () => {
    // The case that actually happens: "Notes" on the chiller and "Notes" on the gondola both derive
    // `notes`, and neither admin typed a key at all.
    expect([...duplicateKeys([{ key: "notes" }, { key: "facings" }, { key: "notes" }])]).toEqual([
      "notes",
    ]);
  });

  it("ignores whitespace, because the server trims before it compares", () => {
    expect(duplicateKeys([{ key: "notes" }, { key: " notes " }]).has("notes")).toBe(true);
  });

  it("does not treat two empty keys as a collision", () => {
    /*
     * Two questions whose text has no letters in it — "???" derives to nothing — are each broken on
     * their own, and the screen says so per question. Calling them duplicates would report a
     * relationship instead of the two independent problems, and renaming one would not fix either.
     */
    expect(duplicateKeys([{ key: "" }, { key: "" }, { key: "  " }]).size).toBe(0);
  });

  it("is empty when every key is distinct", () => {
    expect(duplicateKeys([{ key: "a" }, { key: "b" }]).size).toBe(0);
  });
});

describe("choice types", () => {
  it("is true for both of them and nothing else", () => {
    // Both, deliberately: a caller comparing against SingleChoice alone is how a multi-choice
    // question ends up saved with its options thrown away.
    expect(isChoice("SingleChoice")).toBe(true);
    expect(isChoice("MultiChoice")).toBe(true);

    expect(isChoice("Text")).toBe(false);
    expect(isChoice("Number")).toBe(false);
    expect(isChoice("Boolean")).toBe(false);
    expect(isChoice("Photo")).toBe(false);
  });
});
