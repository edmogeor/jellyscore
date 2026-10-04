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
    Library: "Films",
    Path: "/media/Films/Dialog film/theme.mp3",
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
    Library: "TV",
    Path: "/media/TV/Custom source/theme.mp3",
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
  // Match the live Jellyfin Material fields rather than a separately maintained mock.
  await page.goto(base + "/web/#/dashboard/settings");
  const native = page.locator("input.MuiFilledInput-input[type=text]").first();
  await native.waitFor({ state: "visible" });
  const fieldStyles = (input) =>
    input.evaluate((element) => {
      const root = element.parentElement;
      const label = document.querySelector('label[for="' + element.id + '"]');
      const inputStyle = getComputedStyle(element);
      const rootStyle = getComputedStyle(root);
      const labelStyle = getComputedStyle(label);
      return {
        input: Object.fromEntries(
          [
            "fontFamily",
            "fontSize",
            "lineHeight",
            "letterSpacing",
            "padding",
            "height",
            "boxSizing",
          ].map((key) => [key, inputStyle[key]]),
        ),
        root: Object.fromEntries(
          ["backgroundColor", "borderRadius", "height"].map((key) => [
            key,
            rootStyle[key],
          ]),
        ),
        label: Object.fromEntries(
          [
            "fontFamily",
            "fontSize",
            "lineHeight",
            "letterSpacing",
            "transform",
            "color",
          ].map((key) => [key, labelStyle[key]]),
        ),
        underline: getComputedStyle(root, "::before").borderBottom,
        focusUnderline: getComputedStyle(root, "::after").borderBottom,
      };
    });
  const captureStates = async (input, filledValue = "50") => {
    const states = {};
    for (const [name, value, focus] of [
      ["empty", "", false],
      ["focused", "", true],
      ["filled", filledValue, false],
    ]) {
      await input.fill(value);
      if (!focus) await input.blur();
      await page.mouse.move(0, 0);
      await page.waitForTimeout(250);
      states[name] = await fieldStyles(input);
    }
    await input.hover();
    await page.waitForTimeout(250);
    states.hover = await fieldStyles(input);
    return states;
  };
  const nativeStates = await captureStates(native);
  const buttonStyle = (locator) =>
    locator.evaluate((element) => {
      const style = getComputedStyle(element);
      return Object.fromEntries(
        [
          "fontFamily",
          "fontSize",
          "fontWeight",
          "lineHeight",
          "padding",
          "borderRadius",
          "height",
          "color",
          "backgroundColor",
          "minWidth",
          "textTransform",
          "letterSpacing",
          "boxShadow",
        ].map((key) => [key, style[key]]),
      );
    });
  const nativeButton = await buttonStyle(
    page.locator("button.MuiButton-root[type=submit]"),
  );
  const checkboxStyle = (locator) =>
    locator.evaluate((element) => {
      const style = getComputedStyle(element);
      return {
        padding: style.padding,
        borderRadius: style.borderRadius,
        color: style.color,
        backgroundColor: style.backgroundColor,
        height: element.getBoundingClientRect().height,
        width: element.getBoundingClientRect().width,
      };
    });
  const nativeCheckbox = page.locator(".MuiCheckbox-root").first();
  const nativeCheckboxInput = nativeCheckbox.locator("input");
  const nativeCheckboxStates = {};
  for (const checked of [false, true]) {
    await nativeCheckboxInput.setChecked(checked);
    await nativeCheckboxInput.blur();
    await page.mouse.move(0, 0);
    nativeCheckboxStates[String(checked)] = await checkboxStyle(nativeCheckbox);
  }
  let releaseLoading;
  const loaded = new Promise((resolve) => {
    releaseLoading = resolve;
  });
  await page.route(/\/ThemeSongs\/settings$/, (route) =>
    route.request().method() === "POST"
      ? route.fulfill({ status: 204 })
      : loaded.then(() => route.continue()),
  );
  let scanStatus = {
    Running: false,
    Processed: 0,
    Total: 0,
    Added: 0,
    Failed: 0,
  };
  await page.route(/\/ThemeSongs\/scan$/, (route) =>
    route.fulfill({ json: scanStatus }),
  );
  await page.route(/\/ThemeSongs\/downloads(?:\?|$)/, async (route) => {
    await loaded;
    await route.fulfill({
      json: { Total: items.length, AllTotal: items.length, Items: items },
    });
  });
  let audioAvailable = true;
  await page.route(/\/Items\/[^/]+\/ThemeSongs$/, (route) =>
    route.fulfill({
      json: {
        Items: audioAvailable
          ? [{ Id: "preview-audio", Path: items[0].Path }]
          : [],
      },
    }),
  );
  // A short, valid PCM recording verifies actual decoding and playback without YouTube.
  const wave = Buffer.alloc(44 + 16000 * 20);
  wave.write("RIFF", 0);
  wave.writeUInt32LE(wave.length - 8, 4);
  wave.write("WAVEfmt ", 8);
  wave.writeUInt32LE(16, 16);
  wave.writeUInt16LE(1, 20);
  wave.writeUInt16LE(1, 22);
  wave.writeUInt32LE(8000, 24);
  wave.writeUInt32LE(16000, 28);
  wave.writeUInt16LE(2, 32);
  wave.writeUInt16LE(16, 34);
  wave.write("data", 36);
  wave.writeUInt32LE(16000 * 20, 40);
  let releaseAudio;
  const audioReady = new Promise((resolve) => {
    releaseAudio = resolve;
  });
  await page.route(/\/Items\/preview-audio\/File$/, async (route) => {
    assert.ok(
      route.request().headers().authorization,
      "audio requests use authenticated headers",
    );
    assert.equal(
      new URL(route.request().url()).search,
      "",
      "audio requests do not put credentials in URLs",
    );
    await audioReady;
    return route.fulfill({ contentType: "audio/wav", body: wave });
  });
  await page.route(/\/ThemeSongs\/[^/]+\/edit$/, async (route) => {
    edits.push(route.request().postDataJSON());
    await route.fulfill({ json: { Result: "Replaced" } });
  });
  await page.goto(base + "/web/#/configurationpage?name=JellyScore");
  await page.locator("#themeDownloadsLoading").waitFor({ state: "visible" });
  await page.getByRole("tab", { name: "Settings", exact: true }).click();
  assert.equal(
    await page.locator("#themeSettings").getAttribute("inert"),
    "",
    "settings cannot be edited before loading",
  );
  await page.locator("#themeSettingsLoading").waitFor({ state: "visible" });
  releaseLoading();
  await page.waitForFunction(
    () => document.querySelector("#themeStrength").value !== "",
  );
  const checkbox = page.locator("input[name=enabled]");
  for (const checked of [false, true]) {
    if ((await checkbox.isChecked()) !== checked)
      await checkbox.locator("..").click();
    await checkbox.blur();
    await page.mouse.move(0, 0);
    assert.deepEqual(
      await checkboxStyle(
        page.locator("input[name=enabled] ~ .checkboxOutline"),
      ),
      nativeCheckboxStates[String(checked)],
      "checkbox geometry and colours match native Jellyfin",
    );
  }
  await page.waitForTimeout(300);
  assert.deepEqual(
    await buttonStyle(
      page.getByRole("button", { name: "Save settings", exact: true }),
    ),
    nativeButton,
    "save button matches Jellyfin's large contained button",
  );
  await page.locator("#themeSettingsLoading").waitFor({ state: "hidden" });
  await page.evaluate(() => {
    window.jellyScoreToasts = [];
    const alert = Dashboard.alert;
    Dashboard.alert = (...args) => {
      window.jellyScoreToasts.push(args[0]);
      return alert(...args);
    };
  });
  await page.locator(".themeAdvanced > summary").click();
  for (const id of ["themeStrength", "themeVolume", "themeTvSource"]) {
    const input = page.locator("#" + id);
    const required = await input.evaluate((element) => element.required);
    await input.evaluate((element) => {
      element.required = false;
    });
    assert.deepEqual(
      await captureStates(input, id === "themeVolume" ? "-26" : "50"),
      nativeStates,
      id + " matches native filled Material fields in every state",
    );
    await input.evaluate((element, required) => {
      element.required = required;
    }, required);
  }
  await page.locator("#themeTvSource").fill("");
  await page.locator("#themeStrength").fill("51");
  await page
    .getByRole("button", { name: "Save settings", exact: true })
    .click();
  await page.waitForFunction(() =>
    window.jellyScoreToasts.includes("Settings saved."),
  );
  assert.equal(
    await page.evaluate(() =>
      window.jellyScoreToasts.includes("Settings saved."),
    ),
    true,
    "save confirmation retains its toast",
  );
  await page.locator("#themeVolume").fill("-26");
  await page.locator("#themeVolume").press("ArrowUp");
  assert.equal(
    await page.locator("#themeVolume").inputValue(),
    "-25",
    "native number stepping is preserved",
  );
  await page.locator("#themeStrength").fill("101");
  assert.equal(
    await page
      .locator("#themeStrength")
      .evaluate((input) => input.validity.rangeOverflow),
    true,
  );
  await page.locator("#themeStrength").blur();
  assert.equal(
    await page
      .locator("#themeStrength")
      .evaluate(
        (input) =>
          getComputedStyle(input.parentElement, "::before").borderBottomColor,
      ),
    "rgb(198, 40, 40)",
    "invalid numbers use Jellyfin's error colour",
  );
  await page.locator("#themeStrength").fill("50");
  await page.locator("#themeOverviewTab").focus();
  await page.locator("#themeOverviewTab").press("ArrowRight");
  assert.equal(
    await page.locator("#themeSettingsTab").getAttribute("aria-selected"),
    "true",
  );
  await page.locator("#themeSettingsTab").press("ArrowLeft");
  assert.equal(
    await page.locator("#themeOverviewTab").getAttribute("aria-selected"),
    "true",
  );
  scanStatus = {
    ...scanStatus,
    Running: true,
    Total: 2,
    ActiveItems: [{ Name: "Dialog film", Stage: "stageSearching" }],
  };
  await page.waitForFunction(() =>
    document
      .querySelector("#themeProgress")
      .textContent.startsWith("Scanning 1 of 2"),
  );
  assert.equal(
    await page.locator("#themeScanDetails dd").count(),
    4,
    "primary scan counts keep a stable layout, including zeros",
  );
  assert.equal(
    await page.locator("#themeScanBar").getAttribute("value"),
    null,
    "the first active item is not counted as completed",
  );
  scanStatus = {
    ...scanStatus,
    Processed: 1,
    ActiveItems: [{ Name: "Custom source", Stage: "stageSearching" }],
  };
  await page.waitForFunction(() =>
    document
      .querySelector("#themeProgress")
      .textContent.startsWith("Scanning 2 of 2"),
  );
  assert.equal(
    await page.locator("#themeScanBar").getAttribute("value"),
    "50",
    "the progress bar counts completed items",
  );
  scanStatus = { ...scanStatus, Running: false, ActiveItems: [] };
  await page.locator("#themeScanStart").waitFor({ state: "visible" });
  const dialog = page.locator("#themeSongsEdit");
  const openMenu = async (name) =>
    page.locator('summary[aria-label="More actions for ' + name + '"]').click();
  const edit = async (name) => {
    await openMenu(name);
    await page
      .getByRole("button", {
        name: "Edit YouTube source for " + name,
        exact: true,
      })
      .click();
  };
  await page
    .locator('summary[aria-label="More actions for Dialog film"]')
    .waitFor({ state: "visible" });
  const addedDate = page.locator(
    "#themeRows tr:first-child td:nth-child(2) time",
  );
  assert.equal(await addedDate.getAttribute("datetime"), items[0].Date);
  assert.equal(
    await addedDate.getAttribute("title"),
    new Date(items[0].Date).toLocaleString(),
    "short dates retain the full timestamp on hover",
  );
  assert.equal(
    await addedDate.textContent(),
    new Date(items[0].Date).toLocaleString(undefined, {
      month: "short",
      day: "numeric",
      hour: "numeric",
      minute: "2-digit",
    }),
    "table timestamps include the date and time",
  );
  assert.equal(
    await page.locator("#themeTable th:last-child").textContent(),
    "",
    "the action column has no visible heading",
  );
  assert.equal(
    await page
      .locator("#themeRows tr:first-child .themeRowActions > *")
      .count(),
    1,
    "the row exposes only its overflow menu",
  );
  await openMenu("Dialog film");
  await page
    .getByRole("button", { name: "Play theme for Dialog film", exact: true })
    .waitFor({ state: "visible" });
  await page
    .getByRole("button", { name: "Refresh theme for Dialog film", exact: true })
    .waitFor({ state: "visible" });
  const menuItemStyle = (locator) =>
    locator.evaluate((element) => {
      const style = getComputedStyle(element);
      return Object.fromEntries(
        [
          "display",
          "padding",
          "gap",
          "minHeight",
          "font",
          "color",
          "borderRadius",
        ].map((key) => [key, style[key]]),
      );
    });
  const deleteStyle = await menuItemStyle(
    page.locator("#themeRows tr:first-child button[data-destructive]"),
  );
  await page.keyboard.press("Escape");
  await openMenu("Dialog film");
  await page
    .getByRole("button", { name: "Play theme for Dialog film", exact: true })
    .click();
  const player = page.locator("#themeSongsPlayer");
  await player.waitFor({ state: "visible" });
  await player.locator(".themeAudioSpinner").waitFor({ state: "visible" });
  assert.equal(
    await player.locator(".themeAudioSpinner").getAttribute("aria-label"),
    "Loading audio…",
    "loading is visual but retains an accessible label",
  );
  releaseAudio();
  const audio = await player.locator("audio").elementHandle();
  await page.waitForFunction(
    () => document.querySelector("#themeSongsPlayer audio")?.readyState >= 2,
  );
  assert.equal(
    await audio.evaluate((element) => element.duration),
    20,
    "the preview decodes installed audio",
  );
  await player.locator(".themeAudioSpinner").waitFor({ state: "hidden" });
  await page.waitForFunction(
    () => !document.querySelector("#themeSongsPlayer audio").paused,
  );
  await player.getByRole("button", { name: "Pause", exact: true }).click();
  assert.equal(
    await audio.evaluate((element) => element.paused),
    true,
    "custom pause controls work",
  );
  await player.locator("media-time-slider").press("Home");
  await player.locator("media-time-slider").press("ArrowRight");
  assert.ok(
    await audio.evaluate((element) => element.currentTime > 0),
    "custom seeking supports the keyboard",
  );
  await player.getByRole("button", { name: "Mute", exact: true }).click();
  assert.equal(
    await audio.evaluate((element) => element.muted),
    true,
    "custom mute controls work",
  );
  await player.getByRole("button", { name: "Unmute", exact: true }).click();
  assert.equal(await audio.evaluate((element) => element.muted), false);
  await player
    .getByRole("button", { name: "Play", exact: true })
    .press("Enter");
  await page.waitForFunction(
    () => !document.querySelector("#themeSongsPlayer audio").paused,
  );
  assert.equal(
    await player
      .getByRole("link", { name: "View source for Dialog film", exact: true })
      .getAttribute("href"),
    items[0].Source,
  );
  await player.getByRole("button", { name: "Close", exact: true }).click();
  await player.waitFor({ state: "detached" });
  assert.equal(
    await audio.evaluate(
      (element) => element.paused && !element.hasAttribute("src"),
    ),
    true,
    "closing stops playback and releases the audio",
  );
  audioAvailable = false;
  await openMenu("Dialog film");
  await page
    .getByRole("button", { name: "Play theme for Dialog film", exact: true })
    .click();
  await page.waitForFunction(() =>
    window.jellyScoreToasts.some((text) =>
      text.startsWith("The installed theme"),
    ),
  );
  await player.locator(".themeAudioSpinner").waitFor({ state: "hidden" });
  await player.getByRole("button", { name: "Close", exact: true }).click();
  await player.waitFor({ state: "detached" });
  audioAvailable = true;
  await page.locator("#themeBulkMenu summary").click();
  assert.equal(
    await page.locator("#themeDeleteAll .material-icons").textContent(),
    "delete",
  );
  assert.deepEqual(
    await menuItemStyle(page.locator("#themeDeleteAll")),
    deleteStyle,
    "bulk deletion matches the row's delete menu item",
  );
  const bulkToggle = await page.locator("#themeBulkMenu summary").boundingBox();
  const bulkMenu = await page
    .locator("#themeBulkMenu .themeMenuItems")
    .boundingBox();
  assert.ok(
    bulkMenu.y > bulkToggle.y + bulkToggle.height,
    "the open menu has a gap below its button",
  );
  await page.locator("#themeDeleteAll").click();
  await page.locator("#themeSongsConfirm").waitFor({ state: "visible" });
  assert.equal(
    await page
      .locator("#themeSongsConfirm .formDialogHeaderTitle")
      .textContent(),
    "Delete all managed themes",
  );
  assert.equal(
    await page
      .locator("#themeSongsConfirm .formDialogHeaderTitle")
      .evaluate((element) => getComputedStyle(element).textAlign),
    "center",
  );
  await page
    .locator("#themeSongsConfirm")
    .getByRole("button", { name: "Cancel", exact: true })
    .click();
  await page.locator("#themeSongsConfirm").waitFor({ state: "detached" });
  // Ignore Jellyfin's cancelled requests from navigating away from the home page.
  errors.length = 0;
  await edit("Dialog film");
  await dialog.waitFor({ state: "visible" });
  assert.equal(
    await dialog.locator('input[type="url"]').inputValue(),
    items[0].YouTubeUrl,
  );
  const sourceInput = dialog.locator('input[type="url"]');
  // Let Jellyfin finish the dialog entrance animation and its delayed autofocus.
  await page.waitForTimeout(500);
  await sourceInput.evaluate((element) => {
    element.required = false;
  });
  assert.deepEqual(
    await captureStates(sourceInput, items[0].YouTubeUrl),
    nativeStates,
    "source dialog matches Jellyfin Material fields",
  );
  await sourceInput.evaluate((element) => {
    element.required = true;
  });
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
  await dialog.waitFor({ state: "detached" });
  assert.equal(edits.length, 0, "cancel does not queue reprocessing");

  await openMenu("Dialog film");
  await page
    .getByRole("button", { name: "Delete theme for Dialog film", exact: true })
    .click();
  const deletion = page.locator("#themeSongsConfirm");
  await deletion.waitFor({ state: "visible" });
  await deletion.getByRole("button", { name: "Cancel", exact: true }).click();
  await deletion.waitFor({ state: "detached" });

  await edit("Custom source");
  await dialog.waitFor({ state: "visible" });
  const input = dialog.locator('input[type="url"]');
  assert.equal(
    await input.inputValue(),
    "",
    "custom sources are not prefilled",
  );
  await input.fill("https://example.com/theme.mp3");
  await dialog
    .getByRole("button", { name: "Replace theme", exact: true })
    .click();
  assert.equal(
    await input.evaluate((element) => element.validity.valid),
    false,
  );
  assert.equal(edits.length, 0, "custom URLs cannot queue reprocessing");
  await input.fill("https://youtu.be/bbbbbbbbbbb");
  await dialog
    .getByRole("button", { name: "Replace theme", exact: true })
    .click();
  await page.waitForResponse(/\/ThemeSongs\/[^/]+\/edit$/);
  await dialog.waitFor({ state: "detached" });
  assert.deepEqual(edits, [
    { YouTubeUrl: "https://www.youtube.com/watch?v=bbbbbbbbbbb" },
  ]);
  assert.equal(
    await page.evaluate(() => window.jellyScoreToasts.includes("Replaced")),
    true,
    "replacement feedback retains its toast",
  );

  await page.evaluate(() => {
    const helper = Dashboard.dialogHelper;
    const create = helper.createDialog;
    helper.createDialog = (...args) => {
      helper.createDialog = create;
      throw Error("Test dialog setup failure");
    };
  });
  await edit("Dialog film");
  await openMenu("Dialog film");
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
  await page.setViewportSize({ width: 390, height: 844 });
  await page
    .locator('summary[aria-label="More actions for Dialog film"]')
    .waitFor({ state: "visible" });
  const mobileTitle = await page
    .locator("#themeRows tr:first-child td:first-child")
    .boundingBox();
  const mobileMenu = await page
    .locator("#themeRows tr:first-child summary")
    .boundingBox();
  assert.ok(
    mobileMenu.y < mobileTitle.y + mobileTitle.height,
    "mobile actions sit alongside the item details",
  );
  assert.equal(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
    true,
    "mobile layout does not overflow",
  );
  await openMenu("Dialog film");
  await page
    .getByRole("button", {
      name: "Edit YouTube source for Dialog film",
      exact: true,
    })
    .waitFor({ state: "visible" });
  assert.equal(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
    true,
    "open mobile menus do not overflow",
  );
  await page.keyboard.press("Escape");
  assert.equal(
    await page.locator(".themeMenu[open]").count(),
    0,
    "Escape dismisses overflow menus",
  );
  await page.getByRole("tab", { name: "Settings", exact: true }).click();
  assert.equal(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
    true,
    "mobile settings do not overflow",
  );
  const cookies = page.locator("#themeCookies");
  await cookies.fill("test cookies");
  await cookies.blur();
  await page.mouse.move(0, 0);
  await page.waitForTimeout(250);
  const cookieStyle = await fieldStyles(cookies);
  assert.equal(
    cookieStyle.label.transform,
    nativeStates.filled.label.transform,
    "multiline fields share native floating labels",
  );
  assert.equal(
    cookieStyle.root.backgroundColor,
    nativeStates.filled.root.backgroundColor,
  );
  await cookies.evaluate((input) => {
    input.disabled = true;
  });
  assert.equal(
    await cookies.evaluate(
      (input) =>
        getComputedStyle(input.parentElement, "::before").borderBottomStyle,
    ),
    "dotted",
    "disabled fields retain Material styling",
  );
  await cookies.evaluate((input) => {
    input.disabled = false;
  });
  await page.getByRole("tab", { name: "Overview", exact: true }).click();
  await openMenu("Dialog film");
  await page
    .getByRole("button", { name: "Play theme for Dialog film", exact: true })
    .click();
  await page.waitForFunction(
    () => document.querySelector("#themeSongsPlayer audio")?.readyState >= 2,
  );
  assert.equal(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
    true,
    "the custom player fits on mobile",
  );
  await player.getByRole("button", { name: "Close", exact: true }).click();
  await player.waitFor({ state: "detached" });
  console.log(
    "Material controls, keyboard navigation, mobile layout, dialogs, and custom audio playback passed",
  );
} finally {
  if (errors.length) console.error("Browser errors:", errors);
  await browser.close();
}
