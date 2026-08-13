import { describe, expect, it } from "vitest";

import { identifying, resourceOf, usersIncluding, type User } from "@/lib/api/users";

const MARIA: User = {
  id: "u-1",
  subjectId: "subject-maria",
  email: "maria.ionescu@fieldkit.local",
  displayName: "Maria Ionescu",
  locale: "ro-RO",
  timeZone: "Europe/Bucharest",
  isActive: true,
  roleIds: [],
};

describe("identifying", () => {
  it("tells two people with the same name apart", () => {
    const other: User = {
      ...MARIA,
      id: "u-2",
      subjectId: "subject-maria-2",
      email: "m.ionescu@fieldkit.local",
    };

    expect(identifying(MARIA)).not.toBe(identifying(other));
  });

  it("keeps the name, so the person is still recognisable", () => {
    expect(identifying(MARIA)).toContain("Maria Ionescu");
    expect(identifying(MARIA)).toContain("maria.ionescu@fieldkit.local");
  });

  it("falls back to the name alone when there is no email", () => {
    // The shape `usersIncluding` synthesises for a deactivated rep: enough to keep them selectable,
    // not enough to introduce them. A bare separator dangling off a name would read as a defect.
    expect(identifying({ ...MARIA, email: "" })).toBe("Maria Ionescu");
  });

  it("labels the rep `usersIncluding` adds back without inventing an email", () => {
    const [restored] = usersIncluding([], {
      userId: "subject-departed",
      displayName: "Departed Rep",
    });

    expect(identifying(restored)).toBe("Departed Rep");
  });
});

describe("resourceOf", () => {
  it("takes the part before the colon", () => {
    expect(resourceOf("outlet:write")).toBe("outlet");
  });

  it("leaves a name with no colon alone", () => {
    expect(resourceOf("everything")).toBe("everything");
  });
});
