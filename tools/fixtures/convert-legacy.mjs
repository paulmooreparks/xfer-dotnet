// Converts the two parkscomputing.com documents from XferLang 0.x to 1.0 for the test fixtures
// (DESIGN.md section 15, step 1). The 0.x constructs these files use are few: the document head,
// ~true and ~false, and the explicit value wrapper <"text"> or <'text'>. Anything else is refused
// loudly, so a construct this script does not know is never passed through silently.
//
//   node tools/fixtures/convert-legacy.mjs <0.x file> <1.0 file>

import { readFileSync, writeFileSync } from "node:fs";

const [from, to] = process.argv.slice(2);
let text = readFileSync(from, "utf8");

text = text.replace(/^<! document \{ xferlang "0\.15" \} !>/, "<! xfer 1 !>");
text = text.replace(/~(true|false)\b/g, "$1");

// A wrapped string. The content is kept exactly; the delimiter is chosen by the rule of
// specification 6, rule 10, so the result is what the canonical writer would write.
function delimit(content) {
  if (!content.includes('"')) return `"${content}"`;
  if (!content.startsWith('"') && !content.endsWith('"')) {
    let longest = 0;
    for (const m of content.matchAll(/"+/g)) longest = Math.max(longest, m[0].length);
    const n = longest + 1 === 2 ? 3 : longest + 1;
    return '"'.repeat(n) + content + '"'.repeat(n);
  }
  let longest = 0;
  for (const m of content.matchAll(/"(#+)/g)) longest = Math.max(longest, m[1].length);
  const hashes = "#".repeat(longest + 1);
  return `${hashes}"${content}"${hashes}`;
}
// A wrapper written straight after a key (title<'...'>) needs a space once it is a string,
// since only whitespace, a comment or a bracket may follow a scalar.
const spaced = (offset, whole, literal) => (offset > 0 && !/[\s[{]/.test(whole[offset - 1]) ? ` ${literal}` : literal);
text = text.replace(/<"((?:[^"]|"(?!>))*)">/g, (_, content, offset, whole) => spaced(offset, whole, delimit(content)));
text = text.replace(/<'((?:[^']|'(?!>))*)'>/g, (_, content, offset, whole) => spaced(offset, whole, delimit(content)));

// Refuse anything else that looks like 0.x: a type sigil before a value, or a remaining wrapper.
const leftovers = [...text.matchAll(/(^|[\s[{])([#&*^~?@=:_|][^\s\]}]*)/gm)].map((m) => m[2]).filter((t) => !t.startsWith("//") && !/^#+"/.test(t)); // a hash-run string is 1.0
const wrappers = [...text.matchAll(/<["'#]/g)];
if (leftovers.length > 0 || wrappers.length > 0) {
  console.error("unconverted 0.x constructs:", leftovers.slice(0, 10), wrappers.length);
  process.exit(1);
}

writeFileSync(to, text);
console.log(`converted ${from} -> ${to}`);
