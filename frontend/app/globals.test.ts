import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import postcss, { type Declaration, type Node } from "postcss";
import { describe, expect, it } from "vitest";

/**
 * Guards the cascade-layer discipline in `globals.css`.
 *
 * Cascade layers outrank specificity, and *unlayered* styles outrank every layer. Tailwind emits
 * its utilities into `@layer utilities`, so a single bare rule at the top level of globals.css
 * silently beats them — no error, no warning, no build failure, just utilities that stop working.
 *
 * This is a regression test. An unlayered `* { padding: 0; margin: 0 }` shipped in the Tailwind v4
 * slice and disabled *every* spacing utility in the app: `<main class="… p-6">` computed to
 * `padding: 0px` and shadcn buttons lost their horizontal padding, so cards sat edge-to-edge and
 * button labels were clipped. Nothing caught it, because nothing was broken in a way a test or a
 * type-check could see.
 *
 * Custom properties are exempt: `:root`/`.dark`/`@theme` define the design tokens that utilities
 * *consume*, so they cannot outrank the utilities that read them.
 */

const cssPath = fileURLToPath(new URL("./globals.css", import.meta.url));
const root = postcss.parse(readFileSync(cssPath, "utf8"), { from: cssPath });

function isInsideLayer(node: Node | undefined): boolean {
  for (let current = node; current; current = current.parent as Node | undefined) {
    if (current.type === "atrule" && current.name === "layer") {
      return true;
    }
  }
  return false;
}

/** `body { display: flex }` → `"body { display }"`, for a failure message that points somewhere. */
function describeDeclaration(decl: Declaration): string {
  for (let current: Node | undefined = decl.parent as Node | undefined; current; current = current.parent as Node | undefined) {
    if (current.type === "rule") {
      return `${(current as { selector: string }).selector} { ${decl.prop} }`;
    }
  }
  return `${decl.prop} (line ${decl.source?.start?.line})`;
}

const unlayered = (() => {
  const offenders: string[] = [];
  root.walkDecls((decl) => {
    if (decl.prop.startsWith("--")) return;
    if (!isInsideLayer(decl.parent as Node | undefined)) offenders.push(describeDeclaration(decl));
  });
  return offenders;
})();

describe("globals.css cascade layers", () => {
  it("declares no ordinary property outside a cascade layer", () => {
    expect(
      unlayered,
      "These beat every Tailwind utility regardless of specificity. Wrap them in `@layer base` — " +
        "or delete them if Tailwind's preflight already covers it.",
    ).toEqual([]);
  });

  it("does not re-reset box-sizing, padding or margin — preflight already does", () => {
    const universal = root.nodes.filter(
      (node): node is typeof node & { selector: string } =>
        node.type === "rule" && (node as { selector: string }).selector === "*",
    );

    expect(universal, "an unlayered `*` rule is the exact shape of the original bug").toEqual([]);
  });

  it("keeps the design tokens where utilities can read them", () => {
    const tokens = new Set<string>();
    root.walkDecls((decl) => {
      if (decl.prop.startsWith("--")) tokens.add(decl.prop);
    });

    // A spot-check that the token layer survived the layering change, not an exhaustive list.
    for (const token of ["--background", "--foreground", "--primary", "--radius"]) {
      expect(tokens, `${token} went missing`).toContain(token);
    }
  });
});
