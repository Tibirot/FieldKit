import { fileURLToPath } from "node:url";

import { defineConfig } from "vitest/config";

export default defineConfig({
  /**
   * No `@vitejs/plugin-react`. Vitest transforms `.tsx` with esbuild, which reads
   * `"jsx": "react-jsx"` from `tsconfig.json` and emits the automatic runtime — everything a
   * component test needs, since Fast Refresh and the React Compiler are Next's job and not the test
   * runner's.
   *
   * The plugin was tried first and rejected on its dependencies: v6 pulls Babel 8 through an
   * optional peer, and this repo pins Babel 7 for `babel-plugin-react-compiler`. Taking a
   * `--legacy-peer-deps` install to satisfy a test runner would put the lockfile at odds with the
   * toolchain doc for no capability we are missing.
   */
  resolve: {
    alias: { "@": fileURLToPath(new URL("./", import.meta.url)) },
  },

  test: {
    /**
     * Node by default, jsdom per file.
     *
     * Most of this suite is assertions over pure modules — catalogs, manifests, service-worker
     * output — and none of them need a DOM. Making every file pay for one costs seconds on a suite
     * that currently runs in single-digit seconds, so component tests opt in with a
     * `@vitest-environment jsdom` docblock instead. Explicit at the top of the file that needs it,
     * rather than a glob in here that has to be kept in step with where the tests live.
     */
    environment: "node",

    /**
     * A fixed, deliberately non-UTC timezone.
     *
     * A CI runner is UTC, and under UTC a whole class of date bug is invisible: parsing
     * `2026-01-01T00:00:00` as local midnight and formatting in UTC gives back the same day, so the
     * test passes on the runner and fails on the machine of anyone east of Greenwich. One of those
     * shipped to a branch before it was caught on screen — see `lib/dates.ts`.
     *
     * Set here rather than per file, because it is a property of the whole suite: any test that
     * touches a date should be run somewhere the difference shows. UTC+2 with DST is a normal place
     * for this product's users to be, which makes it a fair default rather than a stress test.
     */
    env: { TZ: "Europe/Bucharest" },

    include: ["**/*.test.{ts,tsx}"],
    exclude: ["node_modules/**", ".next/**"],
    setupFiles: ["./vitest.setup.ts"],
  },
});
