// Run against `make up` with Playwright available through NODE_PATH.
import assert from "node:assert/strict";
import { createRequire } from "node:module";

const { chromium } = createRequire(import.meta.url)("playwright");
const base = process.env.PREVIEW_URL || "http://127.0.0.1:18096";
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage();
const errors = [];
page.on("pageerror", (error) => errors.push(error.message));
const edits = [];
const items = [
  {
    ItemId: "00000000000000000000000000000001",
    Name: "Dialog film",
    Kind: "Movie",
    Year: 2000,
    Date: new Date().toISOString(),
    Status: "Active",
    Source: "https://www.youtube.com/watch?v=aaaaaaaaaaa",
    YouTubeUrl: "https://www.youtube.com/watch?v=aaaaaaaaaaa",
  },
  {
    ItemId: "00000000000000000000000000000002",
    Name: "Custom source",
    Kind: "Series",
    Year: 2000,
    Date: new Date().toISOString(),
    Status: "Active",
    Source: "https://example.com/theme.mp3",
    YouTubeUrl: null,
  },
];

try {
  await page.goto(base + "/web/#/login");
  await page.locator("#txtManualName").fill("user");
  await page.locator("#txtManualPassword").fill("password");
  await page.getByRole("button", { name: "Sign In", exact: true }).click();
  await page.waitForURL(/home/);
  await page.route(/\/ThemeSongs\/downloads(?:\?|$)/, (route) =>
    route.fulfill({
      json: { Total: items.length, AllTotal: items.length, Items: items },
    }),
  );
  await page.route(/\/ThemeSongs\/[^/]+\/edit$/, async (route) => {
    edits.push(route.request().postDataJSON());
    await route.fulfill({ json: { Result: "Replaced" } });
  });
  await page.goto(base + "/web/#/configurationpage?name=JellyScore");
  const dialog = page.locator("#themeSongsEdit");
  const edit = (name) =>
    page.getByRole("button", { name: "Edit " + name, exact: true });
  await edit("Dialog film").waitFor({ state: "visible" });
  // Ignore Jellyfin's cancelled requests from navigating away from the home page.
  errors.length = 0;
  await edit("Dialog film").click();
  await dialog.waitFor({ state: "visible" });
  assert.equal(
    await dialog.locator('input[type="url"]').inputValue(),
    items[0].YouTubeUrl,
  );
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
  await dialog.waitFor({ state: "detached" });
  assert.equal(edits.length, 0, "cancel does not queue reprocessing");

  await page
    .getByRole("button", { name: "Delete theme for Dialog film", exact: true })
    .click();
  const deletion = page.locator("#themeSongsConfirm");
  await deletion.waitFor({ state: "visible" });
  await deletion.getByRole("button", { name: "Cancel", exact: true }).click();
  await deletion.waitFor({ state: "detached" });

  await edit("Custom source").click();
  await dialog.waitFor({ state: "visible" });
  const input = dialog.locator('input[type="url"]');
  assert.equal(
    await input.inputValue(),
    "",
    "custom sources are not prefilled",
  );
  await input.fill("https://example.com/theme.mp3");
  await dialog.getByRole("button", { name: "Save", exact: true }).click();
  assert.equal(
    await input.evaluate((element) => element.validity.valid),
    false,
  );
  assert.equal(edits.length, 0, "custom URLs cannot queue reprocessing");
  await input.fill("https://youtu.be/bbbbbbbbbbb");
  await dialog.getByRole("button", { name: "Save", exact: true }).click();
  await page.waitForResponse(/\/ThemeSongs\/[^/]+\/edit$/);
  await dialog.waitFor({ state: "detached" });
  assert.deepEqual(edits, [
    { YouTubeUrl: "https://www.youtube.com/watch?v=bbbbbbbbbbb" },
  ]);

  await page.evaluate(() => {
    const helper = Dashboard.dialogHelper;
    const create = helper.createDialog;
    helper.createDialog = (...args) => {
      helper.createDialog = create;
      throw Error("Test dialog setup failure");
    };
  });
  await edit("Dialog film").click();
  await page
    .getByRole("button", { name: "Delete theme for Dialog film", exact: true })
    .click();
  await deletion.waitFor({ state: "visible" });
  await deletion.getByRole("button", { name: "Cancel", exact: true }).click();
  await deletion.waitFor({ state: "detached" });
  assert.deepEqual(
    errors,
    [],
    "dialog errors must not escape or block subsequent modals",
  );
  console.log(
    "Native edit and delete dialogs passed: open, cancel, validation, save, and error recovery",
  );
} finally {
  if (errors.length) console.error("Browser errors:", errors);
  await browser.close();
}
