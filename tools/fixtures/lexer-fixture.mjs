// Generates test/ParksComputing.XferLang.Tests/fixtures/lexer.jsonl from the xfer-js lexer, the
// reference implementation, so the .NET lexer is tested against it token for token and
// diagnostic for diagnostic (DESIGN.md section 1, item 5).
//
//   node tools/fixtures/lexer-fixture.mjs <path to a directory holding a bundle of xfer-js
//   src/lexer.ts, src/normalize.ts and src/limits.ts as ES modules>
//
// Build the bundle in the xfer-js repository with:
//   npx tsdown src/lexer.ts src/normalize.ts src/limits.ts --format esm --out-dir <dir> --no-dts --platform neutral
//
// Each line is one case: the normalized text, every token (kind, start and end byte offsets, its
// leading trivia, and for strings the content and delimiter, for keywords which), and every
// diagnostic (code, offset, line, column, message) in order of offset.

import { readdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const bundle = process.argv[2];
if (!bundle) {
  console.error("usage: node lexer-fixture.mjs <bundle dir>");
  process.exit(2);
}
const { tokenize } = await import(pathToFileURL(join(bundle, "lexer.js")).href);
const { normalize } = await import(pathToFileURL(join(bundle, "normalize.js")).href);
const { resolveLimits } = await import(pathToFileURL(join(bundle, "limits.js")).href);

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..", "..");
const limits = resolveLimits();

function caseOf(text, label, tight) {
  const diagnostics = [];
  const tokens = tokenize(text, tight === undefined ? limits : resolveLimits(tight), diagnostics);
  const ordered = diagnostics
    .map((d, i) => ({ d, i }))
    .sort((a, b) => a.d.start.offset - b.d.start.offset || a.i - b.i)
    .map(({ d }) => [d.code, d.start.offset, d.start.line, d.start.column, d.message]);
  return {
    label,
    text,
    limits: tight ?? null,
    tokens: tokens.map((t) => {
      const out = [t.kind, t.start.offset, t.end.offset, t.leading.map((x) => [x.kind, x.start.offset, x.end.offset, x.run ?? 0])];
      if (t.kind === "string") {
        out.push(t.content, t.delimiter.form, t.delimiter.run);
      } else if (t.kind === "keyword") {
        out.push(t.keyword);
      }
      return out;
    }),
    diagnostics: ordered,
  };
}

// A small deterministic generator, the same as xfer-js test/lexer.test.ts uses.
function mulberry32(seed) {
  let a = seed;
  return () => {
    a |= 0;
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const alphabet = [
  ..."{}[]<>!/\"#-._:,+~? \n\t01279axeETZn",
  "inf", "true", "é", "😀", " ", " ", "\u0085", "</", "<!", "!>", "\"\"", "\"\"\"", "#\"", "\"#", "##",
  "2025-08-01", "09:30:00", "T09:30:00Z", "+08:00", "0x", "0b", "0o", "1_0", "e5", "-inf", "nan", "//>", "\f",
  // Every class of non-ASCII and control character, written as escapes so no editor alters them.
  "\u0080", "ÿ", "߿", "ࠀ", " ", " ", "​", " ", "‮", "⁦", "　",
  "퟿", "", "﻿", "�", "￿", "\u{10000}", "\u{10ffff}", "\u{e0001}", "\u0001", "\u001f", "\u007f",
  "\\", "'", "(", ")", "@", "$", "%", "^", "&", "*", "=", "|", "`", ";",
];

// One random case in five runs under tight limits, so the length-limit diagnostics are compared too.
const tightLimits = [
  { runLength: 1, tokenBytes: 1 },
  { runLength: 2, tokenBytes: 3 },
  { runLength: 3, tokenBytes: 2 },
];

const cases = [];

// Every suite document, normalized.
const suite = join(root, "suite", "tests");
function txtar(file) {
  const entries = new Map();
  let name;
  let body = [];
  const lines = readFileSync(join(suite, file), "utf8").split("\n");
  lines.forEach((line, i) => {
    const m = /^-- (.+?) --$/.exec(line);
    if (m) {
      if (name !== undefined) entries.set(name, body.join(""));
      name = m[1];
      body = [];
    } else if (name !== undefined) {
      if (i < lines.length - 1) body.push(`${line}\n`);
      else if (line !== "") body.push(line);
    }
  });
  if (name !== undefined) entries.set(name, body.join(""));
  return entries;
}
for (const file of ["valid.txtar", "invalid.txtar", "to-json.txtar"]) {
  for (const [name, content] of txtar(file)) {
    if (name.endsWith(".xfer")) {
      const n = normalize(content, limits);
      if (n.diagnostics.length === 0) cases.push(caseOf(n.text, `${file}:${name}`));
    }
  }
}
for (const name of readdirSync(join(suite, "bytes"))) {
  if (name.endsWith(".xfer") && !name.endsWith(".canonical.xfer")) {
    const n = normalize(new Uint8Array(readFileSync(join(suite, "bytes", name))), limits);
    // The limit cases are large; keep the fixture small by skipping anything over 64 KB.
    if (n.diagnostics.length === 0 && n.text.length < 65536) cases.push(caseOf(n.text, `bytes:${name}`));
  }
}

// Random texts.
const random = mulberry32(20261019);
for (let run = 0; run < 6000; run++) {
  const pieces = Math.floor(random() * 28);
  let text = "";
  for (let i = 0; i < pieces; i++) {
    text += alphabet[Math.floor(random() * alphabet.length)];
  }
  const tight = run % 5 === 4 ? tightLimits[Math.floor(run / 5) % tightLimits.length] : undefined;
  cases.push(caseOf(text, `random:${run}`, tight));
}

const out = join(root, "test", "ParksComputing.XferLang.Tests", "fixtures", "lexer.jsonl");
writeFileSync(out, `${cases.map((c) => JSON.stringify(c)).join("\n")}\n`);
console.log(`${cases.length} cases written to ${out}`);
