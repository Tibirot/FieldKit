import { afterEach, describe, expect, it, vi } from "vitest";

import { readOidcSettings } from "./settings";

/**
 * Precedence, which is the whole of this module and was wrong in exactly one environment.
 *
 * Service discovery answers "how does a *container* reach Keycloak". In Azure Container Apps that
 * is an internal FQDN, and this value is handed to a **browser**. The deployed app signed in
 * successfully — the first redirect is a navigation — and then died at the first silent renewal,
 * because the discovery document was fetched cross-origin from a host the browser cannot resolve.
 *
 * No test could have caught it before, because there were no tests for this file and no environment
 * in which the two addresses differed. The second is what these encode.
 */
afterEach(() => {
  vi.unstubAllEnvs();
});

describe("readOidcSettings", () => {
  it("prefers KEYCLOAK_URL over service discovery", () => {
    // The deployed shape: both present, and they are different hosts on purpose.
    vi.stubEnv("KEYCLOAK_URL", "https://keycloak.public.example");
    vi.stubEnv("services__keycloak__https__0", "https://keycloak.internal.example");
    vi.stubEnv("services__keycloak__http__0", "http://keycloak.internal.example");

    expect(readOidcSettings()?.authority).toBe("https://keycloak.public.example");
  });

  it("falls back to service discovery, https first", () => {
    // Development: no explicit address, because there is only one address.
    vi.stubEnv("services__keycloak__https__0", "https://localhost:8443");
    vi.stubEnv("services__keycloak__http__0", "http://localhost:8080");

    expect(readOidcSettings()?.authority).toBe("https://localhost:8443");
  });

  it("uses http service discovery when https is absent", () => {
    vi.stubEnv("services__keycloak__http__0", "http://localhost:8080");

    expect(readOidcSettings()?.authority).toBe("http://localhost:8080");
  });

  it("is null when nothing names Keycloak", () => {
    // `next build` runs in this state and must not fail; the sign-in screen explains itself.
    expect(readOidcSettings()).toBeNull();
  });

  it("always uses the realm's client id", () => {
    vi.stubEnv("KEYCLOAK_URL", "https://keycloak.public.example");

    expect(readOidcSettings()?.clientId).toBe("fieldkit-web");
  });
});
