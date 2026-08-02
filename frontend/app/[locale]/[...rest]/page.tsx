import { notFound } from "next/navigation";

/**
 * Makes unmatched paths *match* the `[locale]` segment so they render the localized
 * `[locale]/not-found.tsx` inside the locale layout. Without this, Next falls back to its
 * built-in, untranslated 404 because no segment matched at all.
 */
export default function CatchAllPage() {
  notFound();
}
