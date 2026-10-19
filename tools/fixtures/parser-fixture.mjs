// Generates test/ParksComputing.XferLang.Tests/fixtures/parser.jsonl from xfer-js, the reference
// implementation, so the .NET parser is tested against it: the shape of the lossless tree, every
// diagnostic (code, start and end, message), the version, and the tagged value model.
//
//   node tools/fixtures/parser-fixture.mjs <bundle dir>
//
// Build the bundle in the xfer-js repository with (clean the directory first; --no-config stops
// tsdown also running the repository's own two builds into the same directory, which corrupts it):
//   npx tsdown --no-config src/index.ts test/generator.ts test/tagged.ts test/random.ts --format esm --out-dir <dir> --no-dts --platform neutral

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const bundle = process.argv[2];
if (!bundle) {
  console.error("usage: node parser-fixture.mjs <bundle dir>");
  process.exit(2);
}
const load = (name) => import(pathToFileURL(join(bundle, name)).href);
const { parse } = await load("src/index.js");
const { generate, renderDocument, randomSeparator } = await load("test/generator.js");
const { toTaggedDocument } = await load("test/tagged.js");
const { mulberry32 } = await load("test/random.js");

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..", "..");

// The tree as nested arrays, tokens by their start offset (DESIGN.md section 17, step 5).
const tok = (t) => (t === undefined ? null : t.start.offset);
function annotation(a) {
  return [tok(a.open), tok(a.name), a.value === undefined ? null : node(a.value), tok(a.close)];
}
function node(n) {
  const ann = n.annotations.map(annotation);
  switch (n.kind) {
    case "object":
      return ["o", ann, tok(n.open), tok(n.close), n.pairs.map(pair)];
    case "sequence":
      return ["s", ann, tok(n.open), tok(n.close), n.items.map(node)];
    case "scalar":
      return ["v", ann, tok(n.token)];
    case "error":
      return ["e", ann, n.tokens.map(tok)];
  }
  throw new Error(`unknown node ${n.kind}`);
}
function pair(p) {
  return [
    p.annotations.map(annotation),
    tok(p.key),
    p.skipped.map((s) => ("kind" in s ? ["t", tok(s)] : ["a", annotation(s)])),
    p.value === undefined ? null : node(p.value),
  ];
}
function shape(tree) {
  return [
    tree.head === undefined ? null : annotation(tree.head),
    tree.root === undefined ? null : node(tree.root),
    tree.trailing.map(tok),
    tok(tree.eof),
  ];
}

function caseOf(text, label, limits) {
  const result = parse(text, limits === undefined ? undefined : { limits });
  return {
    label,
    text,
    limits: limits ?? null,
    ok: result.ok,
    version: result.version,
    diagnostics: result.diagnostics.map((d) => [d.code, d.start.offset, d.start.line, d.start.column, d.end.offset, d.end.line, d.end.column, d.message]),
    tree: shape(result.tree),
    value: result.ok ? toTaggedDocument(result) : null,
  };
}

const cases = [];
const seen = new Set();
const add = (text, label, limits) => {
  const key = `${JSON.stringify(limits ?? null)}\u0000${text}`;
  if (!seen.has(key)) {
    seen.add(key);
    cases.push(caseOf(text, label, limits));
  }
};

// Generated documents, rendered with random spacing, then each one damaged a few ways.
const pieces = ["{", "}", "[", "]", "<!", "!>", " ", ",", ":", "x", "1", '"', "#", "id", "tag", "xfer", "\"k\"", "<!-", "</ c />"];
for (let seed = 1; seed <= 1000; seed++) {
  const doc = generate(seed);
  const random = mulberry32(seed * 7919);
  const text = renderDocument(doc, randomSeparator(random), random() < 0.5 ? "" : " \n");
  add(text, `generated:${seed}`, seed % 10 === 0 ? { depth: 2, elements: 6 } : undefined);
  for (let k = 0; k < 3; k++) {
    const at = Math.floor(random() * (text.length + 1));
    const r = random();
    let damaged;
    if (r < 0.34) {
      damaged = text.slice(0, at) + text.slice(at + 1 + Math.floor(random() * 4));
    } else if (r < 0.67) {
      damaged = text.slice(0, at) + pieces[Math.floor(random() * pieces.length)] + text.slice(at);
    } else {
      damaged = text.slice(0, at);
    }
    add(damaged, `damaged:${seed}:${k}`);
  }
}

// Token soup: random sequences of pieces that drive every parser path.
const soup = [
  "{", "}", "[", "]", "<!", "!>", "<! id \"a\" !>", "<! tag t !>", "<! xfer 1 !>", "<! xfer 2 !>", "<! lang en !>", "<! let x 1 !>",
  " ", "\n", "</ c />", ",", ":", "a", "b", "\"s\"", "\"\"", "#\"q\"#", "1", "-2", "1.5", "true", "null", "inf", "nan", "2025-08-01",
  "~", "?", "<x", "\"open", "a.", "0755", "1-2", "!",
  // Non-ASCII text, long keys (the messages shorten them), line endings, a byte order mark in the
  // middle, and head versions the parser must refuse.
  "é", "日本", "\u{1F600}", `"${"\u{1F600}".repeat(15)}"`, `"${"k".repeat(30)}"`, "x".repeat(26), "\r\n", "\r", "\uFEFF",
  "<! xfer 01 !>", "<! xfer 1_0 !>", "<! xfer 0x1 !>", "<! xfer 0 !>", "<! xfer -1 !>", "<! xfer \"1\" !>", "<! id \"a\" !>",
];
const random = mulberry32(20261019);
for (let run = 0; run < 4000; run++) {
  let text = "";
  const n = Math.floor(random() * 16);
  for (let i = 0; i < n; i++) {
    text += soup[Math.floor(random() * soup.length)];
    if (random() < 0.5) text += " ";
  }
  const limits = run % 10 === 9 ? { depth: 1 + (run % 3), elements: 3 + (run % 5) } : undefined;
  add(text, `soup:${run}`, limits);
}

const out = join(root, "test", "ParksComputing.XferLang.Tests", "fixtures", "parser.jsonl");
writeFileSync(out, `${cases.map((c) => JSON.stringify(c)).join("\n")}\n`);
console.log(`${cases.length} cases written to ${out}`);
