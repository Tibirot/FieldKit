import { apiDelete, apiGet, apiSend } from "@/lib/api/client";

/**
 * A user profile (`IAM-01`).
 *
 * **`id` and `subjectId` are different identifiers and are not interchangeable.** `id` is this
 * module's row; `subjectId` is the Keycloak `sub`, and it is what everything outside IAM refers to a
 * person by — a rep assignment among them.
 */
export type User = {
  id: string;
  subjectId: string;
  email: string;
  displayName: string;
  locale: string;
  timeZone: string;
  isActive: boolean;
  roleIds: string[];
};

export const usersKey = (subject: string) => ["users", subject] as const;

export function fetchUsers(accessToken: string, signal?: AbortSignal): Promise<User[]> {
  return apiGet<User[]>("/api/iam/users", accessToken, signal);
}

/**
 * A picker's list of people, guaranteed to contain the one already chosen.
 *
 * **A `<select>` cannot hold a value it has no `<option>` for** — the same rule
 * {@link import("./channels").channelsIncluding} exists for, reached from the other direction. There
 * the option was missing because the list had not arrived yet; here it is missing because the list
 * is deliberately narrower than the stored value. The rep picker offers only *active* users, which
 * is right for a new assignment and wrong for an existing one: deactivate a rep and every assignment
 * they hold opens with an empty picker, because the person it is about was filtered out of it.
 *
 * That is worse than cosmetic. The select is uncontrolled, so with no matching option it settles on
 * `selectedIndex: -1` and reports `""` — an admin extending an end date is told the rep is required,
 * and the obvious way out is to pick somebody else, which silently reassigns the territory.
 *
 * So the stored person is always an option. `displayName` comes from the assignment itself, which
 * carries it precisely because the directory may no longer resolve the subject.
 */
export function usersIncluding(
  loaded: readonly User[] | undefined,
  stored: { userId: string; displayName: string | null } | undefined,
): User[] {
  const users = [...(loaded ?? [])];

  if (!stored || users.some((user) => user.subjectId === stored.userId)) return users;

  // Only the two fields the picker reads are real; the rest exist to satisfy the type. This person
  // is not being edited here, and a form that could edit them would be reading them from elsewhere.
  return [
    {
      id: stored.userId,
      subjectId: stored.userId,
      email: "",
      displayName: stored.displayName ?? stored.userId,
      locale: "",
      timeZone: "",
      isActive: false,
      roleIds: [],
    },
    ...users,
  ];
}

/**
 * How a person is written in a picker: their name, and their email when there is one.
 *
 * **A name is not an identifier.** Two people called Maria Ionescu are two rows a supervisor cannot
 * choose between, and the choice is not cosmetic — it decides whose week is generated, whose
 * territory is assigned, whose calendar is configured. The email is already on the same payload;
 * the picker simply was not reading it.
 *
 * Text rather than markup because an `<option>` holds no elements — "email as secondary text" is
 * not available inside a `<select>`, so the two are joined and the separator does the work.
 *
 * Not a translated string. Both catalogues would carry the identical `{name} — {email}`, and a
 * message key that never differs between locales is a key that will drift out of one of them.
 * Derived at the point of use, for the same reason as {@link resourceOf}.
 *
 * Falls back to the name alone when the email is empty — the shape {@link usersIncluding} builds for
 * a deactivated rep, who is in the list to be *kept*, not to be told apart from anyone.
 */
export function identifying(user: Pick<User, "displayName" | "email">): string {
  return user.email ? `${user.displayName} — ${user.email}` : user.displayName;
}

export type UserWrite = {
  subjectId: string;
  email: string;
  displayName: string;
  locale: string;
  timeZone: string;
  roleIds: string[];
};

/** A role, and the permissions it bundles (`IAM-04`). */
export type Role = {
  id: string;
  name: string;
  /** Seeded so a tenant has a working set from day one (`IAM-06`); editable, but not deletable. */
  isSystemTemplate: boolean;
  permissions: string[];
};

export const rolesKey = (subject: string) => ["roles", subject] as const;

export function fetchRoles(accessToken: string, signal?: AbortSignal): Promise<Role[]> {
  return apiGet<Role[]>("/api/iam/roles", accessToken, signal);
}

export function createUser(accessToken: string, user: UserWrite): Promise<User> {
  return apiSend<User>("POST", "/api/iam/users", accessToken, user);
}

export function updateUser(accessToken: string, id: string, user: UserWrite): Promise<User> {
  return apiSend<User>("PUT", `/api/iam/users/${id}`, accessToken, user);
}

/**
 * Turns an account off, or back on.
 *
 * Its own verb rather than a field on the profile update, and the API agrees: deactivation publishes
 * `UserDeactivated` so Sync releases the bound device (A8). A consequence that size should not be
 * reachable by an unrelated edit to somebody's timezone.
 */
export function setUserActive(accessToken: string, id: string, active: boolean): Promise<User> {
  return apiSend<User>(
    "POST",
    `/api/iam/users/${id}/${active ? "reactivate" : "deactivate"}`,
    accessToken,
    {},
  );
}

/**
 * A permission a module owns — `resource:action` — and what it lets someone do.
 *
 * **The catalogue is code, not data.** There is no endpoint to add one, because a permission nothing
 * enforces is not a permission; this list is contributed by the modules that check it. The
 * description is the only thing standing between "grant everything that sounds plausible" and an
 * informed choice.
 */
export type Permission = {
  name: string;
  description: string;
};

export type RoleWrite = {
  name: string;
  permissions: string[];
};

export const permissionsKey = (subject: string) => ["permissions", subject] as const;

export function fetchPermissions(accessToken: string, signal?: AbortSignal): Promise<Permission[]> {
  return apiGet<Permission[]>("/api/iam/permissions", accessToken, signal);
}

export function createRole(accessToken: string, role: RoleWrite): Promise<Role> {
  return apiSend<Role>("POST", "/api/iam/roles", accessToken, role);
}

export function updateRole(accessToken: string, id: string, role: RoleWrite): Promise<Role> {
  return apiSend<Role>("PUT", `/api/iam/roles/${id}`, accessToken, role);
}

/**
 * Removes a role.
 *
 * Refused for a system template — it is the way back to a working set of roles (`IAM-06`) — and for
 * one users still hold, because `BR-IAM-3` says a user must keep at least one and silently
 * reassigning them would be inventing an admin decision.
 */
export function deleteRole(accessToken: string, id: string): Promise<void> {
  return apiDelete(`/api/iam/roles/${id}`, accessToken);
}

/**
 * The resource a permission is about — the part before the colon.
 *
 * Grouping the toggles by it turns a flat list of thirty checkboxes into a handful of decisions
 * about outlets, users, territories. Derived from the name rather than sent, because the name is
 * already `resource:action` by convention and a second field would be a second thing to keep true.
 */
export function resourceOf(permission: string): string {
  const colon = permission.indexOf(":");

  return colon > 0 ? permission.slice(0, colon) : permission;
}
