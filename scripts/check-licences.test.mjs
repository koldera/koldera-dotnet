import test from "node:test";
import assert from "node:assert/strict";
import { evaluate, normalizeLicence, parseNuspecLicence } from "./check-licences.mjs";

const entry = (name, license) => ({ ecosystem: "nuget", name, version: "1.0.0", license });

test("MIT and Apache-2.0 pass, GPL-2.0-only and the AGPL are reported", () => {
  const bad = evaluate([entry("a", "MIT"), entry("b", "Apache-2.0"), entry("c", "GPL-2.0-only"), entry("d", "AGPL-3.0-only")]);
  assert.deepEqual(bad.map((v) => v.name), ["c", "d"]);
  assert.equal(bad[0].reason, "licence not allowed: GPL-2.0-only");
});

test("an OR expression passes when one alternative is allowed, AND needs all", () => {
  assert.deepEqual(evaluate([entry("a", "(MIT OR GPL-3.0-only)")]), []);
  assert.deepEqual(evaluate([entry("a", "MIT AND GPL-3.0-only")]).map((v) => v.name), ["a"]);
  assert.deepEqual(evaluate([entry("a", "(MIT AND Zlib) OR Apache-2.0")]), []);
});

test("missing or unknown licences fail, a reviewed exception passes", () => {
  assert.equal(evaluate([entry("a", "")])[0].reason, "licence unknown");
  assert.equal(evaluate([entry("a", "SEE LICENSE IN LICENSE.txt")])[0].reason, "licence not allowed: SEE LICENSE IN LICENSE.txt");
  assert.deepEqual(evaluate([entry("a", "")], { exceptions: { "nuget:a": "reviewed: MIT text" } }), []);
});

test("free-form names and URLs are normalised", () => {
  assert.equal(normalizeLicence("The Apache Software License, Version 2.0"), "Apache-2.0");
  assert.equal(normalizeLicence("https://www.apache.org/licenses/LICENSE-2.0.txt"), "Apache-2.0");
  assert.equal(normalizeLicence("MIT-0"), "MIT-0");
});

test("nuspec licence expression and url forms", () => {
  assert.equal(parseNuspecLicence('<package><metadata><license type="expression">MIT</license></metadata></package>'), "MIT");
  assert.equal(parseNuspecLicence("<package><metadata><licenseUrl>https://licenses.nuget.org/MIT</licenseUrl></metadata></package>"), "MIT");
  assert.equal(parseNuspecLicence("<package><metadata></metadata></package>"), "");
});
