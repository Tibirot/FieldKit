"use client";

import { Button } from "@/components/ui/button";

/**
 * Reloads the current URL from the offline fallback page.
 *
 * `router.refresh()` would ask the server for fresh RSC payload — which is exactly the thing that
 * just failed. A full reload re-runs the service worker's navigation route, so it succeeds the
 * moment connectivity returns and re-serves the fallback until then.
 */
export function RetryButton({ label }: { label: string }) {
  return <Button onClick={() => window.location.reload()}>{label}</Button>;
}
