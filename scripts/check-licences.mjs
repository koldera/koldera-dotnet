// Licence gate over the NuGet packages the SDK and its tests restore. The SDK is Apache-2.0, so everything it
// depends on must be permissive; anything else (or unknown) fails until a person reviews it into EXCEPTIONS.
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { homedir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const ALLOWED = new Set([
  "MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "ISC", "MPL-2.0", "0BSD", "CC0-1.0", "Unlicense", "BlueOak-1.0.0",
  "MIT-0", "Zlib",
]);

// Reviewed by hand: "nuget:name" -> why it is acceptable. Keep short.
export const EXCEPTIONS = {
  "nuget:xunit.abstractions": "test-only; nuspec has a licenseUrl only, the xunit project is Apache-2.0",
};

const ALIASES = [
  [/apache/i, "Apache-2.0"],
  [/^(the )?mit license$|opensource\.org\/licen[sc]es\/mit/i, "MIT"],
  [/new bsd|bsd[- ]3|3-clause/i, "BSD-3-Clause"],
  [/simplified bsd|bsd[- ]2|2-clause/i, "BSD-2-Clause"],
  [/licenses\.nuget\.org\/(.+)$/i, null],
];

export function normalizeLicence(raw) {
  const text = String(raw ?? "").trim();
  if (!text) return "";
  if (/^[A-Za-z0-9.+-]+$/.test(text)) return text;
  for (const [re, id] of ALIASES) {
    const m = re.exec(text);
    if (m) return id ?? normalizeLicence(m[1]);
  }
  return text;
}

const EXPRESSION = /^\(?[A-Za-z0-9.+-]+\)?(\s+(AND|OR)\s+\(?[A-Za-z0-9.+-]+\)?)*$/;

// "(A OR B) AND C": OR binds looser than AND, parentheses nest. Returns the first disallowed id, or null if it passes.
function failing(expression) {
  const tokens = expression.replace(/\(/g, " ( ").replace(/\)/g, " ) ").split(/\s+/).filter(Boolean);
  let i = 0;
  const orExpr = () => {
    let bad = andExpr();
    while (tokens[i] === "OR") { i++; const next = andExpr(); bad = bad === null || next === null ? null : bad; }
    return bad;
  };
  const andExpr = () => {
    let bad = atom();
    while (tokens[i] === "AND") { i++; const next = atom(); bad = bad ?? next; }
    return bad;
  };
  const atom = () => {
    const t = tokens[i++];
    if (t === "(") { const r = orExpr(); i++; return r; }
    const id = normalizeLicence(t);
    return ALLOWED.has(id) ? null : id;
  };
  const result = orExpr();
  return i < tokens.length ? expression : result;
}

export function evaluate(entries, { exceptions = EXCEPTIONS } = {}) {
  const out = [];
  for (const e of entries) {
    if (exceptions[`${e.ecosystem}:${e.name}`]) continue;
    const expr = (e.license ?? "").trim();
    if (!expr) { out.push({ ...e, reason: "licence unknown" }); continue; }
    const bad = EXPRESSION.test(expr) ? failing(expr) : (ALLOWED.has(normalizeLicence(expr)) ? null : expr);
    if (bad !== null) out.push({ ...e, reason: `licence not allowed: ${bad}` });
  }
  return out;
}

export function parseNuspecLicence(nuspec) {
  const expr = /<license\s+type="expression"[^>]*>([\s\S]*?)<\/license>/.exec(nuspec)?.[1].trim();
  if (expr) return expr;
  const url = /<licenseUrl>([\s\S]*?)<\/licenseUrl>/.exec(nuspec)?.[1].trim();
  if (!url) return "";
  const n = normalizeLicence(url);
  return n === url ? "" : n;
}

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const run = (cmd, args) => execFileSync(cmd, args, { cwd: root, encoding: "utf8", maxBuffer: 1 << 28, shell: process.platform === "win32" });

function nugetEntries() {
  const cache = process.env.NUGET_PACKAGES ?? path.join(homedir(), ".nuget", "packages");
  const seen = new Map();
  for (const proj of ["src/Koldera.Sdk/Koldera.Sdk.csproj", "tests/Koldera.Sdk.Tests/Koldera.Sdk.Tests.csproj"]) {
    const json = JSON.parse(run("dotnet", ["list", proj, "package", "--include-transitive", "--format", "json"]));
    for (const p of json.projects ?? []) for (const f of p.frameworks ?? []) for (const pkg of [...(f.topLevelPackages ?? []), ...(f.transitivePackages ?? [])]) seen.set(`${pkg.id}@${pkg.resolvedVersion}`, pkg);
  }
  return [...seen.values()].map((pkg) => {
    const id = pkg.id.toLowerCase();
    const nuspec = path.join(cache, id, pkg.resolvedVersion, `${id}.nuspec`);
    return { ecosystem: "nuget", name: pkg.id, version: pkg.resolvedVersion, license: existsSync(nuspec) ? parseNuspecLicence(readFileSync(nuspec, "utf8")) : "" };
  });
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const entries = nugetEntries();
  const bad = evaluate(entries);
  console.log(`${entries.length} NuGet packages checked`);
  for (const v of bad) console.error(`${v.name}@${v.version}: ${v.reason}`);
  process.exit(bad.length || entries.length === 0 ? 1 : 0);
}
