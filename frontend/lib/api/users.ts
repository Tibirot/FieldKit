import { apiGet, apiSend } from "@/lib/api/client";

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
