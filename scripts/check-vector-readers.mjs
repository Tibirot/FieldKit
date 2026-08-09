#!/usr/bin/env node
/**
 * Every shared vector file has a reader in **both** languages (`PRD-08`) — W7 slice 15.
 *
 * This is the one property neither test suite can assert about itself. C# passing says the C# engine
 * agrees with the files it read; TypeScript passing says the same about the files it read. Neither
 * knows whether the other read the same set — so a vector file added with a single reader, or a
 * reader quietly deleted, leaves a rule proven in one language and unchecked in the other while
 * every job stays green.
 *
 * It is a source scan rather than a runtime check on purpose: a runtime check would need both
 * suites to report which files they touched, which is more apparatus than the property deserves and
 * would itself need checking.
 *
 * Run by the `parity` CI job, and standalone with:
 *
 *     node scripts/check-vector-readers.mjs
 */

import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative, sep } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));

/** Where the shared files live, and where each language's readers are looked for. */
const VECTORS = join(root, "vectors");

const READERS = [
  { language: "C#", directory: join(root, "FieldKit.Server.Tests"), extensions: [".cs"] },
  { language: "TypeScript", directory: join(root, "frontend", "lib"), extensions: [".ts", ".tsx"] },
];

function walk(directory, extensions) {
  const found = [];

  for (const entry of readdirSync(directory)) {
    // Build output is a copy of the sources, and would make every file look read twice.
    if (entry === "node_modules" || entry === "bin" || entry === "obj") continue;

    const path = join(directory, entry);

    if (statSync(path).isDirectory()) {
      found.push(...walk(path, extensions));
    } else if (extensions.some((extension) => entry.endsWith(extension))) {
      found.push(path);
    }
  }

  return found;
}

/** Every `*.json` under `vectors/`, by the name a reader would spell. */
const files = walk(VECTORS, [".json"]).map((path) => ({
  path: relative(root, path).split(sep).join("/"),
  name: path.split(sep).at(-1),
}));

if (files.length === 0) {
  console.error("No vector files found under vectors/ — that is either a move or a deletion.");
  process.exit(1);
}

const sources = READERS.map((reader) => ({
  ...reader,
  text: walk(reader.directory, reader.extensions)
    .map((path) => readFileSync(path, "utf8"))
    .join("\n"),
}));

const orphans = [];

for (const file of files) {
  const missing = sources
    .filter((source) => !source.text.includes(file.name))
    .map((source) => source.language);

  if (missing.length > 0) orphans.push({ file: file.path, missing });
}

if (orphans.length > 0) {
  console.error("Shared vector files are not read by both engines:\n");

  for (const orphan of orphans) {
    console.error(`  ${orphan.file} — no reader in ${orphan.missing.join(" or ")}`);
  }

  console.error(
    "\nA file with one reader is a rule proven in one language and unchecked in the other.",
  );
  console.error("Add the missing reader, or delete the file if the rule is gone.");

  process.exit(1);
}

console.log(`${files.length} shared vector file(s), each read by C# and TypeScript.`);
