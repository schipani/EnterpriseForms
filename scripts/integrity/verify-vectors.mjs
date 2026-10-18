#!/usr/bin/env node
// Independent Node.js verifier for the Gate 0 conformance vectors.
// Implements DynamicForms/MerkleLeaf/v1 and DynamicForms/RowHmac/v1 from the normative text
// WITHOUT importing the Python or C# implementations. Zero dependencies (node:crypto only).
// Exit 0 only if every positive case recomputes byte-for-byte and every negative case rejects.
import { createHash, createHmac } from "node:crypto";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "..", "..");
const failures = [];
const check = (cond, msg) => { if (!cond) failures.push(msg); };
const sha256 = (buf) => createHash("sha256").update(buf).digest();
const hex = (buf) => Buffer.from(buf).toString("hex");
const ascii = (s) => Buffer.from(s, "ascii");

class FormatError extends Error {}

const u16 = (n) => { const b = Buffer.alloc(2); b.writeUInt16BE(n); return b; };
const u32 = (n) => { const b = Buffer.alloc(4); b.writeUInt32BE(n); return b; };
const i32 = (n) => { const b = Buffer.alloc(4); b.writeInt32BE(n); return b; };
const u64 = (n) => { const b = Buffer.alloc(8); b.writeBigUInt64BE(BigInt(n)); return b; };
const i64 = (n) => { const b = Buffer.alloc(8); b.writeBigInt64BE(BigInt(n)); return b; };
const uuidBytes = (s) => {
  const h = String(s).replace(/-/g, "").toLowerCase();
  if (!/^[0-9a-f]{32}$/.test(h)) throw new FormatError("invalid uuid");
  return Buffer.from(h, "hex");
};

// ---------------- Merkle v1 ----------------
const LEAF_DOMAIN = ascii("DynamicForms/MerkleLeaf/v1");
const NODE_DOMAIN = ascii("DynamicForms/MerkleNode/v1");
const EMPTY_DOMAIN = ascii("DynamicForms/MerkleEmpty/v1");
const INDEX_DOMAIN = ascii("DynamicForms/ManifestIndex/v1");
const KIND_CODES = new Set([1, 2, 3, 4, 5, 6]);

function itemKey(kind, id) {
  if (!KIND_CODES.has(kind)) throw new FormatError("unknown kind");
  return Buffer.concat([Buffer.from([kind]), uuidBytes(id)]);
}
function leafBytes(l) {
  if (l.sig.length !== 32) throw new FormatError("InvalidSignatureLength");
  if (l.digest.length !== 32) throw new FormatError("digest length");
  const key = itemKey(l.kind, l.id);
  if (key[0] !== l.kind) throw new FormatError("KindCodeMismatch");
  return Buffer.concat([Buffer.from([0]), LEAF_DOMAIN, u16(17), key, l.sig, l.digest, Buffer.from([l.kind]), u64(l.rev), uuidBytes(l.op)]);
}
const leafHash = (l) => sha256(leafBytes(l));
const nodeHash = (a, b) => sha256(Buffer.concat([Buffer.from([1]), NODE_DOMAIN, a, b]));
const emptyRoot = () => sha256(Buffer.concat([Buffer.from([2]), EMPTY_DOMAIN]));
function sortLeaves(leaves) {
  const ordered = [...leaves].sort((a, b) => Buffer.compare(itemKey(a.kind, a.id), itemKey(b.kind, b.id)));
  for (let i = 1; i < ordered.length; i++) {
    if (Buffer.compare(itemKey(ordered[i - 1].kind, ordered[i - 1].id), itemKey(ordered[i].kind, ordered[i].id)) === 0) throw new FormatError("DuplicateItemKey");
  }
  for (const l of ordered) {
    if (l.rev !== ordered[0].rev || l.op.toLowerCase() !== ordered[0].op.toLowerCase()) throw new FormatError("MixedRevisionOrOperation");
  }
  return ordered;
}
function levels(sorted) {
  if (sorted.length === 0) return [[emptyRoot()]];
  let level = sorted.map(leafHash);
  const out = [level];
  while (level.length > 1) {
    const next = [];
    for (let i = 0; i + 1 < level.length; i += 2) next.push(nodeHash(level[i], level[i + 1]));
    if (level.length % 2 === 1) next.push(level[level.length - 1]);
    level = next;
    out.push(level);
  }
  return out;
}
function indexDigest(sorted) {
  const parts = [Buffer.from([3]), INDEX_DOMAIN, u64(sorted.length)];
  for (const l of sorted) parts.push(u16(17), itemKey(l.kind, l.id), leafHash(l));
  return sha256(Buffer.concat(parts));
}
const fromDescriptor = (d) => ({ kind: d.itemKindCode, id: d.itemId, sig: Buffer.from(d.rowSignatureHex, "hex"), digest: Buffer.from(d.canonicalPayloadDigestHex, "hex"), rev: d.committedContentRevision, op: d.committedOperationId });

function verifyMerkle(doc) {
  check(doc.emptyRootHex === hex(emptyRoot()), "merkle: empty root");
  for (const c of doc.cases) {
    for (const d of c.inputLeavesInSubmissionOrder) {
      check(hex(sha256(ascii(d.fixturePayloadAscii))) === d.canonicalPayloadDigestHex, `${c.caseId}: payload digest`);
      check(hex(itemKey(d.itemKindCode, d.itemId)) === d.itemKeyHex, `${c.caseId}: item key`);
    }
    const sorted = sortLeaves(c.inputLeavesInSubmissionOrder.map(fromDescriptor));
    const e = c.expected;
    check(JSON.stringify(sorted.map((l) => hex(itemKey(l.kind, l.id)))) === JSON.stringify(e.sortedItemKeysHex), `${c.caseId}: sorted keys`);
    check(JSON.stringify(sorted.map((l) => hex(leafBytes(l)))) === JSON.stringify(e.leafBytesHex), `${c.caseId}: leaf bytes`);
    check(JSON.stringify(sorted.map((l) => hex(leafHash(l)))) === JSON.stringify(e.leafHashesHex), `${c.caseId}: leaf hashes`);
    const lv = levels(sorted);
    check(JSON.stringify(lv.map((x) => x.map(hex))) === JSON.stringify(e.levelsBottomUpHex), `${c.caseId}: levels`);
    check(hex(lv[lv.length - 1][0]) === e.merkleRootHex, `${c.caseId}: root`);
    check(sorted.length === e.leafCount, `${c.caseId}: count`);
    check(hex(indexDigest(sorted)) === e.leafIndexDigestHex, `${c.caseId}: index digest`);
  }
  for (const n of doc.negativeCases) {
    let threw = false;
    try {
      if (n.caseId === "MRKV1-NEG-DUPLICATE-KEY" || n.caseId === "MRKV1-NEG-MIXED-REVISION") sortLeaves(n.inputLeavesInSubmissionOrder.map(fromDescriptor));
      else if (n.caseId === "MRKV1-NEG-KIND-MISMATCH") { const key = Buffer.from(n.itemKeyHex, "hex"); if (key[0] !== n.trailingItemKindCode) throw new FormatError("KindCodeMismatch"); }
      else if (n.caseId === "MRKV1-NEG-SIGNATURE-LENGTH") { const b = fromDescriptor(doc.cases[0].inputLeavesInSubmissionOrder[1]); leafBytes({ ...b, sig: Buffer.from(n.rowSignatureHex, "hex") }); }
      else check(false, `${n.caseId}: unknown negative`);
    } catch (err) { threw = err instanceof FormatError; }
    check(threw, `${n.caseId}: expected rejection`);
  }
}

// ---------------- Row HMAC v1 ----------------
const ROW_DOMAIN = "DynamicForms/RowHmac/v1";
const TAGS = { Boolean: 1, Int32: 2, Int64: 3, Decimal: 4, Date: 5, TimestampUtc: 6, Uuid: 7, Text: 8, Bytes: 9, Json: 10 };
const asciiLen16 = (s) => { const b = Buffer.from(s, "ascii"); return Buffer.concat([u16(b.length), b]); };

function encodeDecimal(text) {
  const m = /^([+-])?(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$/.exec(String(text).trim());
  if (!m) throw new FormatError("decimal syntax");
  let coefficient = BigInt((m[2] + (m[3] ?? "")).replace(/^0+(?=\d)/, ""));
  let scale = (m[3] ?? "").length - (m[4] ? parseInt(m[4], 10) : 0);
  if (m[1] === "-") coefficient = -coefficient;
  if (coefficient === 0n) scale = 0;
  else while (scale > 0 && coefficient % 10n === 0n) { coefficient /= 10n; scale -= 1; }
  if (scale < 0) { coefficient *= 10n ** BigInt(-scale); scale = 0; }
  // minimal two's complement
  let bytes = [];
  let v = coefficient;
  if (v >= 0n) { do { bytes.unshift(Number(v & 0xffn)); v >>= 8n; } while (v > 0n); if (bytes[0] & 0x80) bytes.unshift(0); }
  else { do { bytes.unshift(Number(v & 0xffn)); v >>= 8n; } while (v < -1n); if (!(bytes[0] & 0x80)) bytes.unshift(0xff); }
  const coef = Buffer.from(bytes);
  return Buffer.concat([i32(scale), u16(coef.length), coef]);
}
function encodeText(s) {
  if (/[\uD800-\uDFFF]/.test(s.replace(/[\uD800-\uDBFF][\uDC00-\uDFFF]/g, ""))) throw new FormatError("surrogate");
  const b = Buffer.from(s.normalize("NFC"), "utf8");
  return Buffer.concat([u32(b.length), b]);
}
function jcs(node) {
  if (node === null) return "null";
  if (typeof node === "boolean") return node ? "true" : "false";
  if (typeof node === "number") {
    if (!Number.isInteger(node)) throw new FormatError("JsonFloatNotPermitted");
    if (Math.abs(node) > Number.MAX_SAFE_INTEGER) throw new FormatError("json int range");
    return String(node);
  }
  if (typeof node === "string") return JSON.stringify(node).replace(/\\u([0-9A-F]{4})/g, (m, h) => "\\u" + h.toLowerCase());
  if (Array.isArray(node)) return "[" + node.map(jcs).join(",") + "]";
  if (typeof node === "object") {
    const keys = Object.keys(node);
    for (const k of keys) if (!/^[\x00-\x7f]*$/.test(k)) throw new FormatError("JsonKeyNotAscii");
    keys.sort(); // ASCII keys: code-unit order equals code-point order
    return "{" + keys.map((k) => JSON.stringify(k) + ":" + jcs(node[k])).join(",") + "}";
  }
  throw new FormatError("json type");
}
function parseTimestampMicros(s) {
  const m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,6}))?(Z|[+-]\d{2}:\d{2})$/.exec(s);
  if (!m) throw new FormatError("TimestampOffsetRequired");
  const ms = Date.UTC(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +m[6]);
  let micros = BigInt(ms) * 1000n + BigInt((m[7] ?? "").padEnd(6, "0"));
  if (m[8] !== "Z") { const sign = m[8][0] === "-" ? 1n : -1n; const [hh, mm] = m[8].slice(1).split(":").map(Number); micros += sign * BigInt((hh * 60 + mm) * 60) * 1_000_000n; }
  return micros;
}
function encodeValue(col, v) {
  const tag = TAGS[col.type];
  if (tag === undefined) throw new FormatError("type");
  if (v === null || v === undefined) { if (!col.nullable) throw new FormatError("NullNotPermitted"); return Buffer.from([0]); }
  const T = Buffer.from([tag]);
  switch (col.type) {
    case "Boolean": return Buffer.concat([T, Buffer.from([v ? 1 : 0])]);
    case "Int32": return Buffer.concat([T, i32(v)]);
    case "Int64": return Buffer.concat([T, i64(v)]);
    case "Decimal": return Buffer.concat([T, encodeDecimal(v)]);
    case "Date": { const [y, mo, d] = v.split("-").map(Number); return Buffer.concat([T, i32(Math.round(Date.UTC(y, mo - 1, d) / 86400000))]); }
    case "TimestampUtc": return Buffer.concat([T, i64(parseTimestampMicros(v))]);
    case "Uuid": return Buffer.concat([T, uuidBytes(v)]);
    case "Text": return Buffer.concat([T, encodeText(v)]);
    case "Bytes": { const b = Buffer.from(v, "hex"); return Buffer.concat([T, u32(b.length), b]); }
    case "Json": { const b = Buffer.from(jcs(v), "utf8"); return Buffer.concat([T, u32(b.length), b]); }
    default: throw new FormatError("unreachable");
  }
}
function loadSchema(s) {
  const names = s.signedColumns.map((c) => c.columnName);
  const sorted = [...names].sort();
  if (JSON.stringify(names) !== JSON.stringify(sorted) || new Set(names).size !== names.length) throw new FormatError("RegistryNotSorted");
  return s;
}
function canonicalRow(schema, { keyVersion, organizationId, primaryKeyId, rowRevision, values }) {
  const registered = new Set(schema.signedColumns.map((c) => c.columnName));
  for (const k of Object.keys(values)) if (!registered.has(k)) throw new FormatError("UnregisteredColumn");
  for (const k of registered) if (!(k in values)) throw new FormatError("MissingColumn");
  const rev = BigInt(rowRevision);
  if (rev < 1n || rev > 0xffffffffffffffffn) throw new FormatError("RowRevisionRange");
  const parts = [Buffer.from([0x10]), asciiLen16(ROW_DOMAIN), u16(schema.schemaVersion), u32(keyVersion), asciiLen16(schema.entityKind), uuidBytes(organizationId), uuidBytes(primaryKeyId), u64(rev), u32(schema.signedColumns.length)];
  for (const col of schema.signedColumns) parts.push(asciiLen16(col.columnName), encodeValue(col, values[col.columnName]));
  return Buffer.concat(parts);
}
// JSON cannot carry 2^64-1 or 2^63 exactly; read big integers via a reviver-free approach.
function readJsonWithBigInts(text) {
  // Quote any integer literal with 16+ digits so JSON.parse keeps it as a string, then convert on use.
  return JSON.parse(text.replace(/(:\s*)(-?\d{16,})(?=[,}\]\s])/g, '$1"$2"'));
}
function verifyRows(doc) {
  const key = Buffer.from(doc.testHmacKeyHex, "hex");
  const schemas = new Map(doc.schemas.map((s) => [`${s.entityKind}:${s.schemaVersion}`, loadSchema(s)]));
  for (const ex of doc.decimalEncodingExamples) check(hex(encodeDecimal(ex.input)) === ex.encodedHex, `decimal ${ex.input}`);
  const byId = {};
  for (const c of doc.cases) {
    const schema = schemas.get(`${c.entityKind}:${c.schemaVersion}`);
    const canonical = canonicalRow(schema, c);
    byId[c.caseId] = c.expected;
    check(hex(canonical) === c.expected.canonicalBytesHex, `${c.caseId}: canonical bytes`);
    check(canonical.length === c.expected.canonicalByteLength, `${c.caseId}: length`);
    check(hex(sha256(canonical)) === c.expected.canonicalPayloadDigestHex, `${c.caseId}: digest`);
    check(createHmac("sha256", key).update(canonical).digest("hex") === c.expected.rowSignatureHexWithTestKey, `${c.caseId}: hmac`);
  }
  check(byId["ROWV1-TYPES-ALL-PRESENT"].canonicalBytesHex === byId["ROWV1-TYPES-NFC-EQUIVALENCE"].canonicalBytesHex, "NFC equivalence");
  check(byId["ROWV1-TYPES-ALL-NULL"].canonicalBytesHex !== byId["ROWV1-EMPTY-STRING-NOT-NULL"].canonicalBytesHex, "empty vs null");
  const cov = schemas.get("integrity_type_coverage:1");
  const base = doc.cases.find((c) => c.caseId === "ROWV1-TYPES-ALL-NULL");
  const common = { keyVersion: 1, organizationId: base.organizationId, primaryKeyId: base.primaryKeyId, rowRevision: 1 };
  const neg = {
    "ROWV1-NEG-NULL-IN-NON-NULLABLE": () => canonicalRow(cov, { ...common, values: { ...base.values, boolean_flag: null } }),
    "ROWV1-NEG-JSON-FLOAT": () => canonicalRow(cov, { ...common, values: { ...base.values, json_value: { a: 1.5 } } }),
    "ROWV1-NEG-JSON-NON-ASCII-KEY": () => canonicalRow(cov, { ...common, values: { ...base.values, json_value: { "é": 1 } } }),
    "ROWV1-NEG-UNSORTED-REGISTRY": () => loadSchema({ entityKind: "x", schemaVersion: 1, signedColumns: [{ columnName: "b_col", type: "Text", nullable: true }, { columnName: "a_col", type: "Text", nullable: true }] }),
    "ROWV1-NEG-EXTRA-COLUMN": () => canonicalRow(cov, { ...common, values: { ...base.values, rogue_column: 1 } }),
    "ROWV1-NEG-MISSING-COLUMN": () => { const v = { ...base.values }; delete v.text_value; return canonicalRow(cov, { ...common, values: v }); },
    "ROWV1-NEG-NAIVE-TIMESTAMP": () => canonicalRow(cov, { ...common, values: { ...base.values, timestamp_value: "2024-01-01T00:00:00" } }),
    "ROWV1-NEG-ROW-REVISION-ZERO": () => canonicalRow(cov, { ...common, rowRevision: 0, values: base.values }),
  };
  for (const n of doc.negativeCases) {
    const fn = neg[n.caseId];
    if (!fn) { check(false, `${n.caseId}: unknown negative`); continue; }
    let threw = false;
    try { fn(); } catch (err) { threw = err instanceof FormatError; }
    check(threw, `${n.caseId}: expected rejection`);
  }
}

const merkle = JSON.parse(readFileSync(path.join(root, "trust_authority", "merkle-v1.test-vectors.json"), "utf8"));
const rows = readJsonWithBigInts(readFileSync(path.join(root, "trust_authority", "row-integrity-v1.test-vectors.json"), "utf8"));
verifyMerkle(merkle);
verifyRows(rows);
if (failures.length) { for (const f of failures) console.error("FAIL:", f); process.exit(1); }
console.log(`NODE VECTOR VERIFICATION PASS (${merkle.cases.length + merkle.negativeCases.length + rows.cases.length + rows.negativeCases.length + rows.decimalEncodingExamples.length + 1} checks across merkle-v1 and row-integrity-v1)`);
