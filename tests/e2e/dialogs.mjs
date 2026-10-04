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
const additions = [];
let finishFirstAdd;
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
  const selectable = [
    {
      ItemId: "00000000000000000000000000000003",
      Name: "New movie",
      Year: 2001,
      Kind: "Movie",
      Library: "Films",
      CustomSourceAvailable: false,
    },
    {
      ItemId: "00000000000000000000000000000004",
      Name: "New show",
      Year: 2005,
      Kind: "Series",
      Library: "Shows",
      CustomSourceAvailable: true,
    },
    {
      ItemId: "00000000000000000000000000000006",
      Name: "New collection",
      Year: null,
      Kind: "Collection",
      Library: "Collections",
      CustomSourceAvailable: false,
    },
  ];
  await page.route(/\/ThemeSongs\/items(?:\?|$)/, (route) => {
    const url = new URL(route.request().url());
    const search = (url.searchParams.get("search") || "").toLowerCase();
    const second = url.searchParams.get("page") === "2";
    route.fulfill({
      json: {
        Items: (second
          ? [
              {
                ...selectable[0],
                ItemId: "00000000000000000000000000000005",
                Name: "Next movie",
              },
            ]
          : selectable
        ).filter((item) => item.Name.toLowerCase().includes(search)),
        HasMore: !second && !search,
      },
    });
  });
  let startedFirstAdd;
  const firstAddStarted = new Promise((resolve) => {
    startedFirstAdd = resolve;
  });
  const firstAddFinished = new Promise((resolve) => {
    finishFirstAdd = resolve;
  });
  await page.route(/\/ThemeSongs\/downloads$/, async (route) => {
    if (route.request().method() !== "POST") return route.fallback();
    additions.push(route.request().postDataJSON());
    if (additions.length === 1) {
      startedFirstAdd();
      await firstAddFinished;
    }
    await route.fulfill({ json: { Result: "Added" } });
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

  const addButton = page.getByRole("button", {
    name: "Add download",
    exact: true,
  });
  assert.equal(
    await addButton.evaluate((button) => button.nextElementSibling.id),
    "themeDeleteAll",
  );
  const addDialog = page.locator("#themeSongsAdd");
  const openAdd = async () => {
    await addButton.click();
    await addDialog.waitFor({ state: "visible" });
    await addDialog.getByRole("button", { name: /New movie/ }).waitFor();
  };
  await openAdd();
  await addDialog
    .getByRole("button", { name: "Next page", exact: true })
    .click();
  await addDialog.getByRole("button", { name: /Next movie/ }).waitFor();
  await addDialog
    .getByRole("button", { name: "Previous page", exact: true })
    .click();
  await addDialog.getByRole("button", { name: /New movie/ }).waitFor();
  await addDialog
    .getByLabel("Search movies, TV shows, and collections")
    .fill("nothing");
  await addDialog.getByText("No results.", { exact: true }).waitFor();
  await addDialog
    .getByLabel("Search movies, TV shows, and collections")
    .fill("New movie");
  await addDialog.getByRole("button", { name: /New movie/ }).click();
  assert.equal(
    await addDialog
      .locator('.themeAddSource option[value="custom"]')
      .isDisabled(),
    true,
  );
  await addDialog.getByRole("button", { name: "Back", exact: true }).click();
  await addDialog.getByRole("button", { name: /New movie/ }).waitFor();
  await addDialog.getByRole("button", { name: "Cancel", exact: true }).click();
  await addDialog.waitFor({ state: "detached" });
  assert.equal(additions.length, 0, "cancel does not add a theme");

  await openAdd();
  await addDialog.getByRole("button", { name: /New movie/ }).click();
  await addDialog.getByRole("button", { name: "Add", exact: true }).click();
  await firstAddStarted;
  await addDialog.waitFor({ state: "detached" });
  assert.equal(await page.locator("#themeDeleteAll").isDisabled(), true);
  await addButton.click();
  await addDialog.getByRole("button", { name: /New show/ }).click();
  await addDialog.getByLabel("Source", { exact: true }).selectOption("custom");
  assert.equal(await addDialog.locator(".themeAddMethod").isVisible(), false);
  await addDialog.getByRole("button", { name: "Add", exact: true }).click();
  await addDialog.waitFor({ state: "detached" });
  await page
    .locator("#themePendingAdds p")
    .filter({ hasText: "Queued: New show" })
    .waitFor();
  assert.equal(
    additions.length,
    1,
    "manual additions wait in the same processing queue",
  );
  finishFirstAdd();
  await page.locator("#themePendingAdds").waitFor({ state: "hidden" });
  assert.deepEqual(additions, [
    {
      ItemId: selectable[0].ItemId,
      Source: "youtube",
      Mode: "automatic",
      YouTubeUrl: null,
    },
    {
      ItemId: selectable[1].ItemId,
      Source: "custom",
      Mode: "automatic",
      YouTubeUrl: null,
    },
  ]);

  await openAdd();
  await addDialog.getByRole("button", { name: /New movie/ }).click();
  await addDialog
    .getByLabel("Theme selection", { exact: true })
    .selectOption("url");
  const addUrl = addDialog.getByLabel("YouTube URL", { exact: true });
  await addUrl.fill("https://example.com/theme.mp3");
  await addDialog.getByRole("button", { name: "Add", exact: true }).click();
  assert.equal(
    await addUrl.evaluate((element) => element.validity.valid),
    false,
  );
  assert.equal(additions.length, 2);
  await addUrl.fill("https://youtu.be/ccccccccccc");
  await addDialog.getByRole("button", { name: "Add", exact: true }).click();
  await addDialog.waitFor({ state: "detached" });
  await page.locator("#themePendingAdds").waitFor({ state: "hidden" });
  assert.deepEqual(additions[2], {
    ItemId: selectable[0].ItemId,
    Source: "youtube",
    Mode: "url",
    YouTubeUrl: "https://www.youtube.com/watch?v=ccccccccccc",
  });

  await openAdd();
  await addDialog
    .getByRole("button", { name: /New collection · Collection · Collections/ })
    .click();
  assert.equal(
    await addDialog
      .locator('.themeAddSource option[value="custom"]')
      .isDisabled(),
    true,
  );
  await addDialog.getByRole("button", { name: "Add", exact: true }).click();
  await addDialog.waitFor({ state: "detached" });
  await page.locator("#themePendingAdds").waitFor({ state: "hidden" });
  assert.deepEqual(additions[3], {
    ItemId: selectable[2].ItemId,
    Source: "youtube",
    Mode: "automatic",
    YouTubeUrl: null,
  });

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
    "Native add, edit, and delete dialogs passed: search, pagination, source/mode selection, queueing, validation, cancel, save, and error recovery",
  );
} finally {
  finishFirstAdd?.();
  if (errors.length) console.error("Browser errors:", errors);
  await browser.close();
}
