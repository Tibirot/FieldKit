import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import tailwindcss from "@tailwindcss/postcss";
import postcss, {
  AtRule,
  type ChildNode,
  type Container,
  type Declaration,
  type Document,
  type Node,
  type Rule,
} from "postcss";
import { beforeAll, describe, expect, it } from "vitest";

/**
 * Two guards over `globals.css`, for two regressions that both shipped and neither of which broke
 * anything a build, a type-check or a lint run could see:
 *
 * 1. **Cascade layers** — an unlayered `* { padding: 0; margin: 0 }` outranked `@layer utilities`
 *    and disabled every spacing utility in the app.
 * 2. **Theme resolution** — the dark palette was reachable only through a `.dark` class that
 *    nothing ever set, so a dark-preferring device rendered the light theme and every `dark:`
 *    utility was dead code.
 *
 * They read the file differently on purpose. The layer guard parses the **source**, because that is
 * where the rule humans must follow lives. The theme guard compiles the CSS the way the build does
 * and asserts on the **emitted** output, because asserting on source would only check spelling.
 */

const globalsPath = fileURLToPath(new URL("./globals.css", import.meta.url));
const source = readFileSync(globalsPath, "utf8");

// ── 1. Cascade layers (source) ──────────────────────────────────────────────

const sourceRoot = postcss.parse(source, { from: globalsPath });

/**
 * The theme blocks, which are exempt from the layering rule.
 *
 * A deliberate carve-out, not a loophole. The palette cascade depends on `:root` and `:root, .dark`
 * landing at *equal specificity* so source order decides the winner; putting them in a layer would
 * break that, and the theme suite below asserts they stay unlayered. `color-scheme` is the only
 * ordinary property they carry, and it has to travel with the palette it describes or native
 * controls fall out of step with the page.
 */
const THEME_BLOCK = /^:root(\s*,\s*\.dark)?$/;

function isThemeBlock(selector: string): boolean {
  return THEME_BLOCK.test(selector.replace(/\s+/g, " ").trim());
}

/** The nearest enclosing rule, or undefined at the top level of the file. */
function enclosingRule(node: Node | undefined): Rule | undefined {
  for (let current = node; current; current = current.parent as Node | undefined) {
    if (current.type === "rule") return current as Rule;
  }
  return undefined;
}

function isInsideLayer(node: Node | undefined): boolean {
  for (let current = node; current; current = current.parent as Node | undefined) {
    if (current.type === "atrule" && (current as AtRule).name === "layer") return true;
  }
  return false;
}

/** `body { display: flex }` → `"body { display }"`, so a failure points somewhere. */
function describeDeclaration(decl: Declaration): string {
  const rule = enclosingRule(decl.parent as Node | undefined);
  return rule
    ? `${rule.selector} { ${decl.prop} }`
    : `${decl.prop} (line ${decl.source?.start?.line})`;
}

const unlayered = (() => {
  const offenders: string[] = [];
  sourceRoot.walkDecls((decl) => {
    if (decl.prop.startsWith("--")) return; // utilities consume tokens; they never conflict
    if (isInsideLayer(decl.parent as Node | undefined)) return;

    const rule = enclosingRule(decl.parent as Node | undefined);
    if (rule && isThemeBlock(rule.selector)) return;

    offenders.push(describeDeclaration(decl));
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
    const universal = sourceRoot.nodes.filter(
      (node) => node.type === "rule" && (node as Rule).selector === "*",
    );

    expect(universal, "an unlayered `*` rule is the exact shape of the original bug").toEqual([]);
  });

  it("exempts only the theme blocks, and nothing else, from that rule", () => {
    // Guards the carve-out itself: if this ever widened to something like `.dark *`, the layering
    // rule would quietly stop applying to real element styles.
    expect(isThemeBlock(":root")).toBe(true);
    expect(isThemeBlock(":root, .dark")).toBe(true);
    expect(isThemeBlock("body")).toBe(false);
    expect(isThemeBlock("*")).toBe(false);
    expect(isThemeBlock(".dark")).toBe(false);
    expect(isThemeBlock(":root .dark")).toBe(false);
  });
});

// ── 2. Theme resolution (compiled) ──────────────────────────────────────────

/**
 * A rule appended to the compiled sheet purely to observe how the `dark:` variant expands. Writing
 * utility names in this file is only safe because `globals.css` excludes `*.test.ts` from
 * Tailwind's source scan — otherwise `underline` and `dark:underline` would ship as real CSS.
 */
const PROBE_SELECTOR = ".fk-dark-variant-probe";
const probe = `${PROBE_SELECTOR} { @apply dark:underline; }`;

/**
 * The two arms the `dark` variant compiles to, spelled out. Asserting the whole selector rather
 * than a substring is deliberate: `:where(.dark)` and `:where(.dark, .dark *)` both "contain
 * `.dark`" while meaning very different things — only the second re-themes a subtree — and the
 * same is true of `:not(.light …)` versus `:not(:root.light …)`, which is the difference between
 * `.light` being a document-root override and a mid-page one.
 */
const PREFERENCE_ARM = ":where(:not(:root.light, :root.light *))";
const CLASS_ARM = ":where(.dark, .dark *)";

/** The palette is authored on `:root, .dark`, which Tailwind folds into an `:is()` list. */
const PALETTE_ANCHOR = ":is(:root, .dark)";

const PREFERS_DARK = "media (prefers-color-scheme: dark)";

type Tokens = Record<string, string>;

let sheet: Container;

beforeAll(async () => {
  const result = await postcss([tailwindcss()]).process(`${source}\n${probe}\n`, {
    from: globalsPath,
  });
  sheet = result.root;
}, 120_000);

/**
 * The at-rules wrapping a node, outermost first. Which at-rules a theme block sits under is not
 * decoration: `@media` gates whether it applies at all, and `@layer` outranks source order, so an
 * unlayered block beats a layered one no matter which came first. Both silently un-apply a palette.
 */
function atRuleContext(node: ChildNode): string[] {
  const context: string[] = [];
  let parent: Container | Document | undefined = node.parent;
  for (; parent; parent = parent.parent) {
    if (parent instanceof AtRule) context.unshift(`${parent.name} ${parent.params}`.trim());
  }
  return context;
}

const underPrefersDark = (node: ChildNode) => atRuleContext(node).includes(PREFERS_DARK);

/**
 * The theme blocks this stylesheet owns, in document order. Tailwind emits its own `:root, :host`
 * block for the default theme, which this deliberately leaves out — matching it would compare
 * FieldKit's palette against Tailwind's spacing and font defaults.
 */
const THEME_ANCHOR = /^(:root(:where\(|$)|:is\(:root[,)])/;

function themeBlocks(): Rule[] {
  const blocks: Rule[] = [];
  sheet.walkRules((rule) => {
    if (THEME_ANCHOR.test(rule.selector)) blocks.push(rule);
  });
  return blocks;
}

function tokens(predicate: (rule: Rule) => boolean): Tokens {
  const collected: Tokens = {};
  for (const rule of themeBlocks().filter(predicate)) {
    rule.walkDecls((decl) => {
      collected[decl.prop] = decl.value;
    });
  }
  return collected;
}

/** Custom properties holding a colour, i.e. the palette rather than `--radius` and friends. */
function paletteKeys(from: Tokens): string[] {
  return Object.entries(from)
    .filter(([prop, value]) => prop.startsWith("--") && value.startsWith("oklch("))
    .map(([prop]) => prop)
    .sort();
}

/** The light palette: unconditional, so it is the floor every other arm overrides. */
const isLight = (rule: Rule) => rule.selector === ":root" && atRuleContext(rule).length === 0;
const isClassDark = (rule: Rule) => rule.selector.includes(".dark") && !underPrefersDark(rule);

const light = () => tokens(isLight);
const systemDark = () => tokens(underPrefersDark);
const classDark = () => tokens(isClassDark);

describe("theme tokens", () => {
  it("applies the dark palette when the device prefers dark", () => {
    // The regression: before the fix this block did not exist at all.
    expect(paletteKeys(systemDark()).length).toBeGreaterThan(0);
    expect(systemDark()["--background"]).toBe("oklch(0.16 0.015 240)");
  });

  it("still applies the dark palette to an explicit `.dark` root", () => {
    // Forward compatibility: a per-user theme toggle must only have to set the class.
    expect(classDark()["--background"]).toBe("oklch(0.16 0.015 240)");
  });

  it("keeps the two dark arms identical", () => {
    expect(systemDark()).toEqual(classDark());
  });

  it("defines the same palette in light and dark", () => {
    expect(paletteKeys(systemDark())).toEqual(paletteKeys(light()));
  });

  it("declares a light palette that is not the dark one", () => {
    expect(light()["--background"]).toBe("oklch(1 0 0)");
  });

  it("orders the dark palette last, so an explicit `.dark` root beats the light one", () => {
    // Every theme block lands at the same specificity, so source order is the whole mechanism.
    const blocks = themeBlocks();
    const lastLight = blocks.findLastIndex(isLight);
    const firstDark = blocks.findIndex((rule) => isClassDark(rule) || underPrefersDark(rule));

    expect(lastLight).toBeGreaterThanOrEqual(0);
    expect(firstDark).toBeGreaterThan(lastLight);
  });

  it("gates each palette on nothing but the device preference", () => {
    // Source order only decides the winner while the blocks are peers. Gating the light palette
    // behind `@media (prefers-color-scheme: light)` would leave a `.light` root with no tokens at
    // all, and moving either block into a `@layer` would let the unlayered one win outright —
    // both leave every assertion above intact. This is the compiled-output counterpart of the
    // theme-block exemption in the layer suite above: the two guards agree on purpose.
    const contexts = themeBlocks().map((rule) => atRuleContext(rule));

    expect(contexts).toContainEqual([]);
    for (const context of contexts) {
      expect(
        [[], [PREFERS_DARK]],
        `a theme block gated on ${JSON.stringify(context)}`,
      ).toContainEqual(context);
    }
  });

  it("keeps `color-scheme` in step with the palette, so native controls follow", () => {
    expect(light()["color-scheme"]).toBe("light");
    expect(systemDark()["color-scheme"]).toBe("dark");
    expect(classDark()["color-scheme"]).toBe("dark");
  });
});

/**
 * The scoping contract, asserted as whole selectors on the two things that must agree: the design
 * tokens and the `dark:` utilities. A substring check passes for `:where(.dark)` as readily as for
 * `:where(.dark, .dark *)`, and the difference is whether a wrapper themes its subtree at all.
 */
describe("the `dark` variant arms", () => {
  // Tailwind carries the authored selector's own line breaks through to the output, so
  // `:root,\n.dark {` and `:root, .dark {` compile to selectors that differ only in whitespace.
  // Comparing normalized keeps this asserting on meaning rather than on formatting.
  const normalize = (selector: string) => selector.replace(/\s+/g, " ").trim();

  const armsFor = (matches: (rule: Rule) => boolean) => {
    const arms: { preference: string[]; class: string[] } = { preference: [], class: [] };
    sheet.walkRules((rule) => {
      if (!matches(rule)) return;
      (underPrefersDark(rule) ? arms.preference : arms.class).push(normalize(rule.selector));
    });
    return arms;
  };

  it("themes a `.dark` subtree and honours a `.light` root, for the design tokens", () => {
    const arms = armsFor((rule) => themeBlocks().includes(rule) && !isLight(rule));

    expect(arms.preference).toEqual([`${PALETTE_ANCHOR}${PREFERENCE_ARM}`]);
    expect(arms.class).toEqual([`${PALETTE_ANCHOR}${CLASS_ARM}`]);
  });

  it("themes a `.dark` subtree and honours a `.light` root, for `dark:` utilities", () => {
    const arms = armsFor((rule) => rule.selector.includes(PROBE_SELECTOR));

    expect(arms.preference).toEqual([`${PROBE_SELECTOR}${PREFERENCE_ARM}`]);
    expect(arms.class).toEqual([`${PROBE_SELECTOR}${CLASS_ARM}`]);
  });
});

describe("Tailwind's source scan", () => {
  it("does not read this file, so the utility names above stay out of the build", () => {
    // The probe writes `dark:underline`, which no component uses. If `globals.css` stopped
    // excluding tests from the scan, the bare `underline` half of it would compile to a real
    // rule and ship — the mistake this test file would otherwise be quietly making.
    const selectors: string[] = [];
    sheet.walkRules((rule) => {
      selectors.push(rule.selector);
    });

    expect(selectors).not.toContain(".underline");
  });
});
