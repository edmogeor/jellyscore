import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { buildSync } from "esbuild";

const [template, dictionary, output] = process.argv.slice(2);
const strings = JSON.parse(readFileSync(dictionary, "utf8"));
let html = readFileSync(template, "utf8").replace(
  /\{\{(\w+)\}\}/g,
  (_, key) => {
    if (typeof strings[key] !== "string")
      throw new Error(`Missing English string: ${key}`);
    return strings[key].replace(
      /[&<>"']/g,
      (character) =>
        ({
          "&": "&amp;",
          "<": "&lt;",
          ">": "&gt;",
          '"': "&quot;",
          "'": "&#39;",
        })[character],
    );
  },
);
// Ship the player inside the existing admin-page resource, without CDN requests.
const videojs = new URL("../node_modules/@videojs/cdn/", import.meta.url);
const player = buildSync({
  entryPoints: [fileURLToPath(new URL("audio-neutral.js", videojs))],
  bundle: true,
  minifyIdentifiers: true,
  minifySyntax: true,
  // Jellyfin localizes ${...} across the whole legacy page, including scripts.
  minifyWhitespace: false,
  supported: { "template-literal": false },
  format: "iife",
  target: "es2022",
  legalComments: "inline",
  write: false,
}).outputFiles[0].text.replace(/<\/script/gi, "<\\/script");
if (player.includes("${")) {
  throw new Error("Player bundle contains Jellyfin localization placeholders");
}
html = html
  .replace(
    "/*__THEME_PLAYER_SCRIPT__*/",
    () => `if (!customElements.get("audio-neutral-skin")) {\n${player}\n}`,
  )
  .replace(
    "<!--__THEME_PLAYER_LICENSES__-->",
    () => `<!--\n${readFileSync(new URL("LICENSE", videojs), "utf8")}\n-->`,
  );
mkdirSync(dirname(output), { recursive: true });
writeFileSync(output, html);
