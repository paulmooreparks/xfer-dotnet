// Generates test/ParksComputing.XferLang.Tests/fixtures/json.jsonl from xfer-js, the reference
// implementation, so the .NET JSON conversions are tested against it: for fromJson the canonical
// form of the result (or no value) and every diagnostic with its position and message; for toJson
// the compact, indented and big-integer-as-string texts and their diagnostics.
//
//   node tools/fixtures/json-fixture.mjs <bundle dir>
//
// Build the bundle in the xfer-js repository exactly as parser-fixture.mjs says (clean the
// directory first; --no-config is required).

import { writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const bundle = process.argv[2];
if (!bundle) {
  console.error("usage: node json-fixture.mjs <bundle dir>");
  process.exit(2);
}
const load = (name) => import(pathToFileURL(join(bundle, name)).href);
const { parse, canonical, fromJson, toJson } = await load("src/index.js");
const { generate, renderDocument, randomSeparator } = await load("test/generator.js");
const { mulberry32 } = await load("test/random.js");

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const diag = (d) => [d.code, d.start.offset, d.start.line, d.start.column, d.message];

const cases = [];
const seen = new Set();

function fromCase(json, label, limits) {
  const key = `f\u0000${JSON.stringify(limits ?? null)}\u0000${json}`;
  if (seen.has(key)) return;
  seen.add(key);
  const r = fromJson(json, limits === undefined ? undefined : { limits });
  cases.push({
    kind: "from",
    label,
    // UTF-16 little-endian, base64: a JSON text may hold a lone surrogate, which a JSON reader of
    // the fixture (System.Text.Json) refuses to read from a string.
    json: Buffer.from(json, "utf16le").toString("base64"),
    limits: limits ?? null,
    canonical: r.value === undefined ? null : canonical(r.value, limits === undefined ? undefined : { limits }),
    diagnostics: r.diagnostics.map(diag),
  });
}

function toCase(xfer, label) {
  const key = `t\u0000${xfer}`;
  if (seen.has(key)) return;
  seen.add(key);
  const result = parse(xfer);
  if (!result.ok) return;
  const value = result.value;
  const compact = toJson(value);
  const big = toJson(value, { stringifyBigInts: true });
  const indented = toJson(value, { indent: 2 });
  const wide = toJson(value, { indent: 15 });
  cases.push({
    kind: "to",
    label,
    xfer,
    compact: [compact.text, compact.diagnostics.map(diag)],
    big: [big.text, big.diagnostics.map(diag)],
    indent2: [indented.text, indented.diagnostics.map(diag)],
    indent15: [wide.text, wide.diagnostics.map(diag)],
  });
}

const random = mulberry32(20261019);
const pick = (items) => items[Math.floor(random() * items.length)];

// Random JSON values written by hand, so that numbers keep their spelling and strings carry escapes.
const NUMBERS = ["0", "-0", "1", "-7", "42", "9007199254740992", "9007199254740993", "-9007199254740993", "123456789012345678901234567890", "1.5", "-0.25", "1e5", "1E-5", "2.5e+3", "1.0", "0e0", "10", "100000000000000000000.5"];
const STRING_BODIES = ["", "a", "hello world", "say \\\"hi\\\"", "tab\\there", "nl\\nx", "back\\\\slash", "\\/", "\\u00e9\\u65e5", "\\ud83d\\ude00", "\\u0001", "\\u001f", "é日😀", " ", "\u007f", "x".repeat(30), "\\r", "\\ud800", "\\udc00", "a\\ud800b", "\\u000d", "\\u0000"];
function genValue(depth) {
  const r = random();
  if (depth <= 0 || r < 0.4) {
    const s = random();
    if (s < 0.3) return pick(NUMBERS);
    if (s < 0.65) return `"${pick(STRING_BODIES)}"`;
    return pick(["true", "false", "null"]);
  }
  const ws = () => pick(["", " ", "\n", "\r\n", "\t ", "  "]);
  const n = Math.floor(random() * 5);
  if (r < 0.7) {
    const items = Array.from({ length: n }, () => genValue(depth - 1));
    return `[${ws()}${items.join(`${ws()},${ws()}`)}${ws()}]`;
  }
  const keys = ["a", "b", "k", "key with space", "é", "", "a", "__proto__", "x/y", "\\u0041"];
  const members = Array.from({ length: n }, () => `"${pick(keys)}"${ws()}:${ws()}${genValue(depth - 1)}`);
  return `{${ws()}${members.join(`${ws()},${ws()}`)}${ws()}}`;
}

const DAMAGE = [",", "]", "}", "{", "[", '"', ":", "-", "+", ".", "e", "E", "0", "t", "f", "n", " ", "\\", "\n", "\u0000", "é", "😀", "//", "NaN", "'"];
for (let run = 0; run < 2200; run++) {
  const text = genValue(4);
  const limits = run % 9 === 8 ? { depth: 1 + (run % 3) } : undefined;
  fromCase(text, `valid:${run}`, limits);
  for (let k = 0; k < 2; k++) {
    const at = Math.floor(random() * (text.length + 1));
    const r = random();
    const damaged =
      r < 0.34 ? text.slice(0, at) + text.slice(at + 1 + Math.floor(random() * 3)) : r < 0.67 ? text.slice(0, at) + pick(DAMAGE) + text.slice(at) : text.slice(0, at);
    fromCase(damaged, `damaged:${run}:${k}`, limits);
  }
}
for (let run = 0; run < 1500; run++) {
  let text = "";
  const n = Math.floor(random() * 12);
  for (let i = 0; i < n; i++) text += pick(DAMAGE.concat(NUMBERS, ["true", "null", "{", "[", '"a"', "}", "]", ":", ","]));
  fromCase(text, `soup:${run}`);
}
// Specific shapes: nesting at and past tight limits, empty and whitespace-only input, a byte order mark.
for (const depth of [1, 2, 3, 4, 10, 255, 256, 257, 300]) {
  fromCase("[".repeat(depth) + "]".repeat(depth), `deep-seq:${depth}`);
  fromCase('{"a":'.repeat(depth) + "1" + "}".repeat(depth), `deep-obj:${depth}`);
}
for (const text of ["", " ", "\n\n", "﻿{}", "﻿", "1 2", "[1,2", '{"a":1,"a":2,"b":3,"a":4}', '"\\u12"', '"\\x"', "-", "--1", "01", "1.", ".5", "+1", "1e", "1e+", "[,]", "[1,]", '{"a"}', '{"a":}', "tru", "nul", "truefalse", "[true false]", "{\"a\":1 \"b\":2}"]) {
  fromCase(text, `edge:${JSON.stringify(text)}`);
}

// toJson from generated documents and from the soup of valid XferLang pieces.
for (let seed = 1; seed <= 700; seed++) {
  const doc = generate(seed);
  const r = mulberry32(seed * 7919);
  toCase(renderDocument(doc, randomSeparator(r), ""), `generated:${seed}`);
}
const PIECES = ["{", "}", "[", "]", "a", "b", "k", "1", "-5", "0xFF", "0o17", "0b101", "1_000", "9007199254740992", "9007199254740993", "-9007199254740993", "123456789012345678901234567890", "1.5", "1e5", "inf", "-inf", "nan", "true", "false", "null", '"s"', '"say ""hi"""', '"line\nbreak"', "sym", "2025-08-01", "09:30:00", "2025-08-01T09:30:00Z", "2025-08-01T09:30:00", "<! id \"a\" !>", "<! tag t !>", "</ c />", '"é日"', '"\t"', '"\\"'];
for (let run = 0; run < 5000; run++) {
  let text = "";
  const n = 1 + Math.floor(random() * 8);
  for (let i = 0; i < n; i++) text += `${pick(PIECES)} `;
  toCase(text, `soup:${run}`);
}
for (const text of ["inf", "[ inf nan -inf ]", "{ a inf }", '{ "a/b" [ x 2025-08-01 ] "~c" nan }', "[ [ [ [ x ] ] ] ]", "{}", "[]", "[ [] {} ]", "-0", "0x7fffffffffffffff", "9007199254740992", "-9007199254740992"]) {
  toCase(text, `edge:${text}`);
}

const out = join(root, "test", "ParksComputing.XferLang.Tests", "fixtures", "json.jsonl");
writeFileSync(out, `${cases.map((c) => JSON.stringify(c)).join("\n")}\n`);
console.log(`${cases.length} cases (${cases.filter((c) => c.kind === "from").length} from, ${cases.filter((c) => c.kind === "to").length} to) written to ${out}`);
