import { createNavigation } from "next-intl/navigation";

import { routing } from "./routing";

/**
 * Locale-aware replacements for `next/link` and `next/navigation`. Always import navigation from
 * here rather than from Next directly — these keep the active locale on the URL for you.
 */
export const { Link, redirect, usePathname, useRouter, getPathname } =
  createNavigation(routing);
