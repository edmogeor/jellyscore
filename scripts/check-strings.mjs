import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";

const plugin = new URL("../Jellyfin.Plugin.JellyScore/", import.meta.url);
const directory = new URL("Strings/", plugin);
const files = readdirSync(directory).filter((name) => name.endsWith(".json"));
const dictionaries = new Map(
  files.map((name) => {
    const text = readFileSync(new URL(name, directory), "utf8");
    const keys = [...text.matchAll(/^\s*"([^"\n]+)"\s*:/gm)].map(
      (match) => match[1],
    );
    assert.equal(
      new Set(keys).size,
      keys.length,
      `${name}: duplicate string keys`,
    );
    const dictionary = JSON.parse(text);
    for (const [key, value] of Object.entries(dictionary)) {
      assert.ok(
        typeof value === "string" && value.trim(),
        `${name}: empty or invalid ${key}`,
      );
    }
    return [name, dictionary];
  }),
);
const english = dictionaries.get("en-us.json");
const keys = Object.keys(english).sort();
const placeholders = (value) =>
  [...new Set(value.match(/\{(?:\d+|[A-Za-z]\w*)\}/g) || [])].sort();
for (const [name, dictionary] of dictionaries) {
  assert.deepEqual(
    Object.keys(dictionary).sort(),
    keys,
    `${name}: keys differ from English`,
  );
  for (const key of keys) {
    assert.deepEqual(
      placeholders(dictionary[key]),
      placeholders(english[key]),
      `${name}: ${key} placeholders differ`,
    );
  }
}

const consumers =
  readdirSync(plugin)
    .filter((name) => /\.(cs|html)$/.test(name))
    .map((name) => readFileSync(new URL(name, plugin), "utf8"))
    .join("\n") +
  readFileSync(new URL("embed-english.mjs", import.meta.url), "utf8");
// The icon tooltip keys are composed as "kind" + item.Kind in the page.
const dynamicKeys = new Set(["kindMovie", "kindSeries", "kindCollection"]);
for (const match of consumers.matchAll(
  /\bt\(\s*"(\w+)"\s*[,)]|data-i18n(?:-[\w-]+)?="(\w+)"/g,
)) {
  const key = match[1] ?? match[2];
  assert.ok(Object.hasOwn(english, key), `Missing UI string: ${key}`);
}
for (const key of keys) {
  assert.ok(
    dynamicKeys.has(key) ||
      consumers.includes('"' + key + '"') ||
      consumers.includes("'" + key + "'") ||
      consumers.includes("." + key),
    `Unused English string: ${key}`,
  );
}
for (const [, code] of consumers.matchAll(/Code\s*=\s*"(\w+)"/g)) {
  assert.ok(Object.hasOwn(english, code), `Missing API error string: ${code}`);
}
console.log(
  `Validated ${files.length} locales and ${keys.length} strings: usage, keys, and placeholders`,
);
