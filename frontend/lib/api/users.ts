import { apiGet } from "@/lib/api/client";

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
