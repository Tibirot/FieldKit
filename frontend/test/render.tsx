import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render as rtlRender } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";

import en from "@/messages/en.json";

/**
 * Renders a component the way the app does.
 *
 * **The real message catalog, not a stub.** A component asking for a key nobody translated should
 * fail here, the same way it renders a raw key in the browser — mocking `useTranslations` to echo
 * its argument would make every one of those tests pass and quietly delete the assertion.
 *
 * A fresh `QueryClient` per render, with retries off. Sharing one across tests leaks cached results
 * between them, and retries turn an intentional failure into a three-second test that passes anyway.
 *
 * Passed as RTL's `wrapper` rather than wrapping the children here, because `rerender` replaces the
 * whole tree with whatever it is handed — a hand-wrapped render loses its providers the moment a
 * test re-renders, and fails with a missing-context error that points at the component.
 */
export function render(ui: React.ReactNode) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });

  function Providers({ children }: { children: React.ReactNode }) {
    return (
      <NextIntlClientProvider locale="en" messages={en}>
        <QueryClientProvider client={client}>{children}</QueryClientProvider>
      </NextIntlClientProvider>
    );
  }

  return rtlRender(ui, { wrapper: Providers });
}
