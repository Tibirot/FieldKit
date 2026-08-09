/**
 * A no-op stand-in for the `server-only` package.
 *
 * `server-only` is not a dependency in `package.json` — Next resolves the specifier itself, and its
 * whole job is to *fail the build* when a server module is pulled into a client bundle. Vitest has
 * no such concept and no such resolver, so importing it throws and the file under test cannot be
 * loaded at all.
 *
 * Aliased in `vitest.config.ts`. The guarantee is not weakened: it is enforced by `next build`,
 * which still sees the real one.
 */
export {};
