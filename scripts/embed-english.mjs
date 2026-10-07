import { readFileSync, writeFileSync, mkdirSync, readdirSync } from "node:fs";
import { dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { buildSync, transformSync } from "esbuild";

const [template, dictionary, output] = process.argv.slice(2);
const strings = JSON.parse(readFileSync(dictionary, "utf8"));
const htmlEntities = {
  "&": "&amp;",
  "<": "&lt;",
  ">": "&gt;",
  '"': "&quot;",
  "'": "&#39;",
};
let html = readFileSync(template, "utf8").replace(
  /\{\{(\w+)\}\}/g,
  (_, key) => {
    if (typeof strings[key] !== "string")
      throw new Error(`Missing English string: ${key}`);
    return strings[key].replace(
      /[&<>"']/g,
      (character) => htmlEntities[character],
    );
  },
);
// Ship the player inside the existing admin-page resource, without CDN requests.
const videojs = new URL("../node_modules/@videojs/cdn/", import.meta.url);
const localeImports = ['import "@videojs/html/i18n";'];
for (const file of readdirSync(
  new URL("../Jellyfin.Plugin.JellyScore/Strings/", import.meta.url),
)) {
  if (!file.endsWith(".json") || file === "en-us.json") continue;
  const locale = Intl.getCanonicalLocales(file.replace(/\.json$/, ""))[0];
  localeImports.push('import "./locales/' + locale + '.js";');
}
const skin = readFileSync(new URL("audio-neutral.js", videojs), "utf8");
// Customize the skin at build time, rather than hiding controls in its shadow DOM.
const playerSkin = skin
  .replace(/<media-playback-rate-button\b[\s\S]*?<\/media-menu>/, "")
  .replace(
    /<media-hotkey\b[^>]*action="speed(?:Up|Down)"[^>]*>\s*<\/media-hotkey>/g,
    "",
  )
  .replace(
    "<media-buffering-indicator",
    '<media-buffering-indicator part="loading-indicator" role="status" aria-label="' +
      strings.loadingAudio +
      '"',
  )
  .replace("<media-play-button", '<media-play-button part="play-button"')
  .replace(
    '<media-icon family="neutral" name="spinner"',
    '<media-icon part="loading-icon" family="neutral" name="spinner"',
  );
if (playerSkin === skin || playerSkin.includes("<media-playback-rate-button")) {
  throw new Error("Video.js playback-speed controls were not removed");
}
if (
  !["loading-indicator", "loading-icon", "play-button"].every((part) =>
    playerSkin.includes('part="' + part + '"'),
  )
) {
  throw new Error("Video.js loading controls were not exposed");
}
const player = buildSync({
  stdin: {
    contents: localeImports.join("\n") + "\n" + playerSkin,
    resolveDir: fileURLToPath(videojs),
    sourcefile: "audio-neutral.js",
  },
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
})
  .outputFiles[0].text.replace(/<\/script/gi, "<\\/script")
  // Keep literal markers in vendor strings from being treated as page translations.
  .replaceAll("${", "\\x24{");
if (player.includes("${")) {
  throw new Error("Player bundle contains Jellyfin localization placeholders");
}
transformSync(player, { loader: "js", target: "es2022" });
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
