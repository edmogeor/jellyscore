"""Exercise plugin discovery and the admin boundary in an actual Jellyfin 12 server."""

import os
import subprocess
import time
import uuid
from setup import request, wizard, libraries

PLUGIN = "129e8a8b-87f1-48d3-802b-7dd151d72920"


def field(data, name):
    return data.get(name, data.get(name[0].upper() + name[1:]))


def assert_settings(token, enabled, libraries, minimum=50, loudness=-26, scan_on_library_refresh=True):
    for path in ("/ThemeSongs/settings", f"/Plugins/{PLUGIN}/Configuration"):
        status, settings = request("GET", path, token=token)
        assert status == 200, f"read settings {path}: {status}"
        assert field(settings, "enabled") is enabled, f"saved enabled in {path}: {settings}"
        assert field(settings, "scanOnLibraryRefresh") is scan_on_library_refresh, f"saved scan trigger in {path}: {settings}"
        assert [uuid.UUID(str(value)) for value in field(settings, "libraries")] == [uuid.UUID(value) for value in libraries], (
            f"saved libraries in {path}: {settings}"
        )
        strength = field(settings, "minimumMatchStrength")
        assert (strength if strength is not None else settings.get("EffectiveMinimumMatchStrength")) == minimum, (
            f"saved minimum match strength in {path}: {settings}"
        )
        target = field(settings, "targetLufs")
        assert (target if target is not None else settings.get("EffectiveTargetLufs")) == loudness, (
            f"saved target loudness in {path}: {settings}"
        )


def scan_until(token, expected=None, expect_current=False):
    _, before = request("GET", "/ThemeSongs/scan", token=token)
    status, _ = request("POST", "/ThemeSongs/scan", token=token)
    assert status == 202, f"start rescan: {status}"
    saw_current = False
    for attempt in range(300):
        status, progress = request("GET", "/ThemeSongs/scan", token=token)
        if status == 200 and field(progress, "running") and field(progress, "currentItem"):
            active = field(progress, "activeItems")
            assert len(active) == 1 and all(field(item, "name") and field(item, "stage") in english_strings for item in active), (
                f"scan omitted active item stages: {progress}"
            )
            setup_stage = field(progress, "toolSetupStage")
            assert setup_stage is None or setup_stage in english_strings, f"unknown tool preparation stage: {progress}"
            assert field(progress, "startedAt").startswith("20"), f"scan start time missing: {progress}"
            if field(progress, "total") > field(progress, "processed"):
                assert field(progress, "remainingSeconds") > 0, f"running scan has no ETA: {progress}"
            saw_current |= "Sorcerer" in field(progress, "currentItem")
        if status == 200 and not field(progress, "running") and field(progress, "runId") != field(before, "runId"):
            assert field(progress, "processed") == field(progress, "total") == 4, f"scan did not process all items: {progress}"
            assert field(progress, "finishedAt") and not field(progress, "cancelled") and not field(progress, "stoppedReason"), (
                f"scan did not report successful completion: {progress}"
            )
            counts = ("added", "alreadyThemed", "excluded", "noMatch", "unsupported", "failed")
            assert sum(field(progress, key) for key in counts) == 4, f"scan counts disagree: {progress}"
            if field(progress, "excluded") + field(progress, "noMatch"):
                assert any(
                    field(item, "name") and field(item, "code") in english_strings and field(item, "at") and
                    field(item, "failed") is False and not field(item, "diagnostic") for item in field(progress, "issues")
                ), (
                    f"scan omitted timestamped skipped items: {progress}"
                )
            assert not expected or field(progress, expected) >= 1, f"Rescan finished without {expected}: {progress}"
            assert not expect_current or saw_current, f"scan never exposed current item: {progress}"
            return progress
        if attempt % 5 == 4:
            print(f"Waiting for scan ({expected or 'completion'}): {progress}", flush=True)
        time.sleep(2)
    raise SystemExit(f"Rescan did not finish with {expected}: {progress}")


for _ in range(60):
    status, info = request("GET", "/System/Info/Public")
    if status == 200:
        break
    time.sleep(2)
else:
    raise SystemExit("Jellyfin did not start")

wizard()

for _ in range(30):
    status, login = request("POST", "/Users/AuthenticateByName", {"Username": "user", "Pw": "password"})
    if status == 200:
        break
    time.sleep(2)
assert status == 200, f"admin login: {status}"
assert login["User"]["Name"] == "user", f"test admin was not renamed: {login['User']['Name']}"
token = login["AccessToken"]
status, config = request("GET", f"/Plugins/{PLUGIN}/Configuration", token=token)
assert status == 200, f"plugin not loaded: {status}"
assert config["Enabled"] is True, "automatic processing must default to on"
assert config["ScanOnLibraryRefresh"] is True, "library-refresh scanning must default to on"
assert config.get("Libraries") is None, "new installs must default to all libraries"
assert config["MinimumMatchStrength"] == 50, "new installs default to match strength 50"
assert config["TargetLufs"] == -26, "new installs default to quieter themes"
assert config["PreferFranchiseThemes"] is False, "movie franchise preference defaults off"
status, settings = request("GET", "/ThemeSongs/settings", token=token)
assert status == 200, f"admin settings: {status}"
assert settings.get("downloaderAvailable", settings.get("DownloaderAvailable")) is True, "downloader release metadata missing"
assert field(settings, "downloaderError") is None and field(settings, "runtimeError") is None, "download tools have no initial error"
assert field(settings, "minimumMatchStrength") == 50, "admin settings expose the effective match strength"
assert field(settings, "targetLufs") == -26, "admin settings expose the effective loudness target"
assert field(settings, "scanOnLibraryRefresh") is True, "admin settings expose the default scan trigger"
assert field(settings, "preferFranchiseThemes") is False, "admin settings expose the movie preference"
assert field(settings, "youTubeCookies") is None, "cookies are optional by default"
assert field(settings, "tvThemeUrlTemplate") is None, "TV theme URL is optional by default"
status, _ = request("POST", "/ThemeSongs/settings", {"enabled": True, "libraries": [], "minimumMatchStrength": -1}, token)
assert status == 400, f"negative match strength must be rejected: {status}"
status, _ = request("POST", "/ThemeSongs/settings", {"enabled": True, "libraries": [], "minimumMatchStrength": 101}, token)
assert status == 400, f"out-of-range match strength must be rejected: {status}"
for target in (-71, -4):
    status, _ = request("POST", "/ThemeSongs/settings", {"enabled": True, "libraries": [], "targetLufs": target}, token)
    assert status == 400, f"unsupported loudness target {target} must be rejected: {status}"
status, error = request("POST", "/ThemeSongs/settings", {"enabled": True, "libraries": [], "youTubeCookies": "not a cookie file"}, token)
assert status == 400 and field(error, "code") == "invalidCookies", f"invalid cookie file must be rejected: {status} {error}"
for template in ("http://example.com/{tvdbId}.mp3", "https://127.0.0.1/{tvdbId}.mp3", "https://example.com/theme.mp3"):
    status, error = request("POST", "/ThemeSongs/settings", {"enabled": True, "libraries": [], "tvThemeUrlTemplate": template}, token)
    assert status == 400 and field(error, "code") == "invalidTvThemeUrl", f"invalid TV theme URL must be rejected: {status} {error}"
status, strings = request("GET", "/ThemeSongs/strings/en-us", token=token)
assert status == 200 and strings["scanLibraries"] == "Scan libraries", f"English translations: {status} {strings}"
assert strings["automatic"] == "Automatically process new items" and strings["scanOnLibraryRefresh"] == "Scan after Jellyfin scans the media library"
assert strings["preferFranchiseThemes"] == "Prefer franchise themes for movies in TMDb collections", "franchise setting has English copy"
english_strings = strings
for url in ("https://example.com/theme.mp3", "https://youtube.com.evil.example/watch?v=aaaaaaaaaaa", "https://www.youtube.com/playlist?list=test", ""):
    status, error = request("POST", "/ThemeSongs/00000000-0000-0000-0000-000000000001/edit", {"youTubeUrl": url}, token)
    assert status == 400 and field(error, "code") == "invalidYouTubeUrl", f"custom or invalid edit source rejected: {status} {error}"
status, strings = request("GET", "/ThemeSongs/strings/fr", token=token)
assert status == 200 and strings["scanLibraries"] == "Analyser les bibliothèques", f"French translations: {status} {strings}"
assert strings["scanOnLibraryRefresh"], "French scan trigger translation missing"
assert strings["cookiesLabel"] and strings["invalidCookies"], "French cookie settings translation missing"
assert "TMDb" in strings["preferFranchiseThemes"], "French franchise setting translation missing"
status, _ = request("GET", "/ThemeSongs/strings/zz", token=token)
assert status == 404, f"unsupported translation should fall back to English: {status}"
status, downloads = request("GET", "/ThemeSongs/downloads", token=token)
assert status == 200 and downloads.get("total", downloads.get("Total")) == 0, f"empty managed list: {status}"
status, deleted = request("DELETE", "/ThemeSongs/downloads", token=token)
assert status == 200 and field(deleted, "deleted") == field(deleted, "skipped") == 0, f"empty bulk delete: {status} {deleted}"
status, _ = request("POST", "/ThemeSongs/settings", {"enabled": False, "libraries": []}, token)
assert status == 204, f"save settings: {status}"
selected = ["00000000-0000-0000-0000-000000000001"]
status, _ = request("POST", "/ThemeSongs/settings", {"enabled": True, "libraries": selected}, token)
assert status == 204, f"enable automatic processing: {status}"
assert_settings(token, True, selected)
status, _ = request("POST", "/ThemeSongs/settings", {"enabled": True, "libraries": selected, "minimumMatchStrength": 75}, token)
assert status == 204, f"save a custom match strength: {status}"
assert_settings(token, True, selected, 75)
status, _ = request("POST", "/ThemeSongs/settings", {"enabled": True, "libraries": selected, "targetLufs": -30}, token)
assert status == 204, f"save a custom loudness target: {status}"
assert_settings(token, True, selected, 75, -30)
cookies = "# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t2147483647\tVISITOR_INFO1_LIVE\ttest\n"
status, _ = request("POST", "/ThemeSongs/settings", {"enabled": True, "libraries": selected, "youTubeCookies": cookies}, token)
assert status == 204, f"save optional cookies: {status}"
status, settings = request("GET", "/ThemeSongs/settings", token=token)
assert status == 200 and field(settings, "youTubeCookies") == cookies, "saved cookies remain editable by the admin"
tv_template = "https://example.com/themes/{tvdbId}.mp3"
status, _ = request("POST", "/ThemeSongs/settings", {"enabled": True, "libraries": selected, "tvThemeUrlTemplate": tv_template}, token)
assert status == 204, f"save optional TV theme URL: {status}"
status, settings = request("GET", "/ThemeSongs/settings", token=token)
assert status == 200 and field(settings, "tvThemeUrlTemplate") == tv_template, "TV theme URL is saved"
status, _ = request("POST", "/ThemeSongs/settings", {"enabled": True, "scanOnLibraryRefresh": False, "libraries": selected}, token)
assert status == 204, f"disable library-refresh scanning: {status}"
assert_settings(token, True, selected, 75, -30, False)
status, _ = request("POST", "/ThemeSongs/settings", {"enabled": True, "libraries": selected, "preferFranchiseThemes": True}, token)
assert status == 204, f"enable franchise preference: {status}"
status, settings = request("GET", "/ThemeSongs/settings", token=token)
assert status == 200 and field(settings, "preferFranchiseThemes") is True, "franchise preference is saved"
status, _ = request("POST", f"/Plugins/{PLUGIN}/Configuration", {"Enabled": True, "ScanOnLibraryRefresh": False, "Libraries": selected, "MinimumMatchStrength": None, "TargetLufs": None, "YouTubeCookies": cookies, "TvThemeUrlTemplate": tv_template}, token)
assert status == 204, f"simulate an existing config with an unset match strength: {status}"
subprocess.run(["docker", "compose", "-f", "tests/e2e/compose.yaml", "restart", "jellyfin"], check=True)
for _ in range(60):
    status, _ = request("GET", "/ThemeSongs/settings", token=token)
    if status == 200:
        break
    time.sleep(2)
assert status == 200, f"plugin did not restart: {status}"
assert_settings(token, True, selected, scan_on_library_refresh=False)
status, settings = request("GET", "/ThemeSongs/settings", token=token)
assert status == 200 and field(settings, "youTubeCookies") == cookies, "saved cookies survive a Jellyfin restart"
assert field(settings, "tvThemeUrlTemplate") == tv_template, "TV theme URL survives a Jellyfin restart"
status, _ = request("POST", "/ThemeSongs/settings", {"enabled": True, "libraries": selected, "youTubeCookies": ""}, token)
assert status == 204, f"clear optional cookies: {status}"
status, settings = request("GET", "/ThemeSongs/settings", token=token)
assert status == 200 and field(settings, "youTubeCookies") is None, "clearing the field removes saved cookies"
status, _ = request("POST", "/ThemeSongs/settings", {"enabled": True, "libraries": selected, "tvThemeUrlTemplate": ""}, token)
assert status == 204, f"clear TV theme URL: {status}"
status, settings = request("GET", "/ThemeSongs/settings", token=token)
assert status == 200 and field(settings, "tvThemeUrlTemplate") is None, "clearing the field removes TV theme URL"
status, before = request("GET", "/ThemeSongs/scan", token=token)
assert status == 200, f"read scan status: {status}"
status, _ = request("POST", "/ThemeSongs/settings", {"enabled": True, "libraries": []}, token)
assert status == 204, f"clear selected libraries: {status}"
status, tasks = request("GET", "/ScheduledTasks", token=token)
assert status == 200, f"list scheduled tasks: {status}"
previous_refresh = next(task for task in tasks if field(task, "key") == "RefreshLibrary")["LastExecutionResult"]
status, _ = request("POST", "/Library/Refresh", token=token, timeout=120)
assert status in (200, 204), f"start Jellyfin library scan: {status}"
for _ in range(60):
    status, tasks = request("GET", "/ScheduledTasks", token=token)
    refresh = next(task for task in tasks if field(task, "key") == "RefreshLibrary")
    if field(refresh, "state") == "Idle" and refresh["LastExecutionResult"] != previous_refresh:
        break
    time.sleep(1)
else:
    raise AssertionError("Jellyfin library scan did not complete")
status, progress = request("GET", "/ThemeSongs/scan", token=token)
assert status == 200 and field(progress, "runId") == field(before, "runId"), f"disabled library trigger started a JellyScore scan: {progress}"
status, _ = request("POST", "/ThemeSongs/settings", {"enabled": False, "scanOnLibraryRefresh": True, "libraries": []}, token)
assert status == 204, f"enable library-refresh scanning without new-item processing: {status}"
assert_settings(token, False, [], scan_on_library_refresh=True)
status, _ = request("POST", "/Library/Refresh", token=token, timeout=120)
assert status in (200, 204), f"start Jellyfin library scan: {status}"
for _ in range(60):
    status, progress = request("GET", "/ThemeSongs/scan", token=token)
    if status == 200 and field(progress, "runId") != field(before, "runId"):
        break
    time.sleep(1)
else:
    raise AssertionError("JellyScore did not run after Jellyfin's library scan")
assert_settings(token, False, [])
for method, path in [("GET", "/ThemeSongs/downloads"), ("DELETE", "/ThemeSongs/downloads"), ("GET", "/ThemeSongs/strings/en-us"), ("POST", "/ThemeSongs/scan"), ("POST", "/ThemeSongs/settings"), ("POST", "/ThemeSongs/downloader/retry"), ("POST", "/ThemeSongs/00000000-0000-0000-0000-000000000001/edit")]:
    status, _ = request(method, path)
    assert status in (401, 403), f"unauthorized {path}: {status}"
print("Jellyfin 12 plugin smoke checks passed")
if os.environ.get("LIVE_YOUTUBE") == "0":
    raise SystemExit(0)
print("Creating movie and TV libraries...", flush=True)
libraries(token)
print("Waiting for movie and series indexing...", flush=True)
status, folders = request("GET", "/Library/VirtualFolders", token=token)
assert status == 200, f"list libraries: {status}"
library_ids = [next(folder["ItemId"] for folder in folders if folder["Name"] == name) for name in ("Films", "Shows")]
status, _ = request("POST", f"/Plugins/{PLUGIN}/Configuration", {"Enabled": False, "Libraries": None, "MinimumMatchStrength": 50}, token)
assert status == 204, f"reset library selection to default: {status}"
status, settings = request("GET", "/ThemeSongs/settings", token=token)
assert status == 200 and {uuid.UUID(str(value)) for value in field(settings, "libraries")} == {
    uuid.UUID(folder["ItemId"]) for folder in folders
}, f"all libraries should be selected by default: {settings}"
status, _ = request("POST", "/ThemeSongs/settings", {"enabled": False, "scanOnLibraryRefresh": False, "libraries": library_ids}, token)
assert status == 204, f"select libraries: {status}"
assert_settings(token, False, library_ids, scan_on_library_refresh=False)
status, tasks = request("GET", "/ScheduledTasks", token=token)
previous_refresh = next(task for task in tasks if field(task, "key") == "RefreshLibrary")["LastExecutionResult"]
status, _ = request("POST", "/Library/Refresh", token=token, timeout=120)
assert status in (200, 204), f"scan media libraries: {status}"

for attempt in range(30):
    status, items = request("GET", "/Items?Recursive=true&IncludeItemTypes=Movie,Series", token=token)
    indexed = {(item["Type"], item["Name"], item.get("ProductionYear")) for item in (items or {}).get("Items", [])}
    if status == 200 and {
        ("Movie", "Harry Potter and the Sorcerer's Stone", 2001),
        ("Movie", "Dune", 2021),
        ("Movie", "User Theme", 2000),
        ("Series", "Star Trek: The Next Generation", 1987),
        ("Movie", "Unselected Example", 1999),
    }.issubset(indexed):
        break
    if attempt % 5 == 0:
        print(f"Waiting for test items: {status}, indexed={indexed}", flush=True)
    time.sleep(2)
else:
    raise SystemExit(f"Test media was not indexed: {status} {items}")
for _ in range(60):
    status, tasks = request("GET", "/ScheduledTasks", token=token)
    refresh = next(task for task in tasks if field(task, "key") == "RefreshLibrary")
    if field(refresh, "state") == "Idle" and refresh["LastExecutionResult"] != previous_refresh:
        break
    time.sleep(1)
else:
    raise AssertionError("Jellyfin library indexing did not finish")
status, folders = request("GET", "/Library/VirtualFolders", token=token)
films = next(folder for folder in folders if folder["Name"] == "Films")
options = films["LibraryOptions"]
options["TypeOptions"] = [{"Type": "Movie", "MetadataFetchers": ["TheMovieDb"]}]
status, _ = request("POST", "/Library/VirtualFolders/LibraryOptions", {"Id": films["ItemId"], "LibraryOptions": options}, token)
assert status == 204, f"enable TMDb metadata provider for film fixture: {status}"
status, _ = request("POST", "/ThemeSongs/settings", {"enabled": False, "scanOnLibraryRefresh": True, "libraries": library_ids}, token)
assert status == 204, f"re-enable library-refresh scanning after fixture indexing: {status}"
source_template = os.environ.get("TV_THEME_URL_TEMPLATE")
if source_template:
    status, _ = request("POST", "/ThemeSongs/settings", {"enabled": False, "scanOnLibraryRefresh": True, "libraries": library_ids,
                                                 "tvThemeUrlTemplate": source_template}, token)
    assert status == 204, f"enable test TV theme source: {status}"
movie = next(item for item in items["Items"] if "Sorcerer" in item["Name"])
user_theme = next(item for item in items["Items"] if item["Name"] == "User Theme")
first_scan = scan_until(token, "added", expect_current=True)
status, downloads = request("GET", "/ThemeSongs/downloads", token=token)
assert status == 200, f"list themes: {status}"
assert all(field(item, "itemId") != user_theme["Id"] for item in field(downloads, "items")), (
    f"user-provided theme became managed: {downloads}"
)
status, error = request("DELETE", f"/ThemeSongs/{user_theme['Id']}", token=token)
assert status == 409 and field(error, "code") == "themeUnavailable", f"delete user-provided theme: {status} {error}"
status, error = request("POST", f"/ThemeSongs/{user_theme['Id']}/edit", {"youTubeUrl": "https://youtu.be/aaaaaaaaaaa"}, token)
assert status == 409 and field(error, "code") == "themeUnavailable", f"edit user-provided theme: {status} {error}"
subprocess.run(["docker", "compose", "-f", "tests/e2e/compose.yaml", "exec", "-T", "jellyfin", "cmp", "-s",
                "/tmp/user-theme-original", "/media/movies/User Theme (2000)/theme.mp3"], check=True)
dune = next((item for item in field(downloads, "items") if field(item, "name") == "Dune"), None)
assert dune is not None and field(dune, "status") == "Active", f"Dune soundtrack track was not discovered: {downloads}"
theme = next((item for item in field(downloads, "items") if uuid.UUID(field(item, "itemId")) == uuid.UUID(movie["Id"])), None)
if theme is None and field(first_scan, "failed"):
    scan_until(token)
    status, downloads = request("GET", "/ThemeSongs/downloads", token=token)
    theme = next((item for item in field(downloads, "items") if uuid.UUID(field(item, "itemId")) == uuid.UUID(movie["Id"])), None)
assert theme is not None, f"test movie theme not downloaded: {downloads}"
if source_template:
    series_theme = next((item for item in field(downloads, "items") if field(item, "kind") == "Series"), None)
    assert series_theme is not None and field(series_theme, "source") == source_template.replace("{tvdbId}", "71470"), (
        f"TV theme URL was not selected before YouTube: {downloads}"
    )
    assert field(series_theme, "youTubeUrl") is None, f"custom TV source must not prefill the YouTube edit field: {series_theme}"
assert field(theme, "source").startswith("https://www.youtube.com/watch?v="), theme
assert field(theme, "youTubeUrl") == field(theme, "source"), f"YouTube edit field must prefill the existing source: {theme}"
original_source = field(theme, "source")
assert field(theme, "score") > 0, theme
assert field(theme, "status") == "Active", theme
with open("dist/universal/yt-dlp-version", encoding="utf-8") as release:
    binary = f"/config/plugins/configurations/jellyscore-yt-dlp/{release.read().strip()}/yt-dlp_linux"
subprocess.run(["docker", "compose", "-f", "tests/e2e/compose.yaml", "exec", "-T", "jellyfin", "test", "-x", binary], check=True)
status, _ = request("POST", "/ThemeSongs/downloader/retry", token=token)
assert status == 204, f"retry endpoint should reuse the verified download: {status}"
status, songs = request("GET", f"/Items/{movie['Id']}/ThemeSongs", token=token)
assert status == 200 and songs.get("TotalRecordCount", 0) > 0, f"Jellyfin cannot see downloaded theme: {status} {songs}"
scan_until(token, "alreadyThemed")
status, refreshed = request("POST", f"/ThemeSongs/{movie['Id']}/refresh", token=token, timeout=300)
assert status == 200 and field(refreshed, "result") in ("Replaced", "No replacement found"), f"refresh theme: {status} {refreshed}"
status, downloads = request("GET", "/ThemeSongs/downloads", token=token)
theme = next((item for item in field(downloads, "items") if uuid.UUID(field(item, "itemId")) == uuid.UUID(movie["Id"])), None)
assert status == 200 and theme is not None, f"refresh lost managed theme: {downloads}"
assert field(theme, "status") == "Active", downloads
status, edited = request("POST", f"/ThemeSongs/{movie['Id']}/edit", {"youTubeUrl": original_source}, token=token, timeout=300)
assert status == 200 and field(edited, "result") == "Replaced", f"reprocess explicitly selected, previously excluded source: {status} {edited}"
status, downloads = request("GET", "/ThemeSongs/downloads", token=token)
theme = next(item for item in field(downloads, "items") if uuid.UUID(field(item, "itemId")) == uuid.UUID(movie["Id"]))
assert field(theme, "source") == original_source and field(theme, "status") == "Active", f"edited source not installed: {theme}"
status, _ = request("DELETE", f"/ThemeSongs/{movie['Id']}", token=token)
assert status == 204, f"delete managed theme: {status}"
status, downloads = request("GET", "/ThemeSongs/downloads", token=token)
assert status == 200 and all(uuid.UUID(field(item, "itemId")) != uuid.UUID(movie["Id"]) for item in field(downloads, "items")), (
    f"deleted theme still managed: {downloads}"
)
for _ in range(30):
    status, songs = request("GET", f"/Items/{movie['Id']}/ThemeSongs", token=token)
    if status == 200 and songs.get("TotalRecordCount") == 0:
        break
    time.sleep(2)
assert status == 200 and songs.get("TotalRecordCount") == 0, f"Jellyfin still sees deleted theme: {status} {songs}"
status, _ = request("POST", f"/ThemeSongs/{movie['Id']}/refresh", token=token)
assert status == 409, f"refresh deleted theme: {status}"
scan_until(token)
status, downloads = request("GET", "/ThemeSongs/downloads", token=token)
theme = next((item for item in field(downloads, "items") if uuid.UUID(field(item, "itemId")) == uuid.UUID(movie["Id"])), None)
assert status == 200 and (theme is None or field(theme, "status") == "Active"), f"rescan produced stale theme: {downloads}"

subprocess.run(["docker", "compose", "-f", "tests/e2e/compose.yaml", "exec", "-T", "jellyfin", "sh", "-c",
                'printf "%s" external-edit >> "$1"', "sh", field(dune, "path")], check=True)
subprocess.run(["docker", "compose", "-f", "tests/e2e/compose.yaml", "exec", "-T", "jellyfin", "cp",
                field(dune, "path"), "/tmp/edited-theme-original"], check=True)
status, error = request("POST", f"/ThemeSongs/{field(dune, 'itemId')}/refresh", token=token)
assert status == 409 and field(error, "code") == "themeChanged", f"refresh externally edited theme: {status} {error}"
status, error = request("POST", f"/ThemeSongs/{field(dune, 'itemId')}/edit", {"youTubeUrl": field(dune, "source")}, token)
assert status == 409 and field(error, "code") == "themeChanged", f"edit externally modified theme: {status} {error}"
status, error = request("DELETE", f"/ThemeSongs/{field(dune, 'itemId')}", token=token)
assert status == 409 and field(error, "code") == "themeChanged", f"delete externally edited theme: {status} {error}"
subprocess.run(["docker", "compose", "-f", "tests/e2e/compose.yaml", "exec", "-T", "jellyfin", "cmp", "-s",
                "/tmp/edited-theme-original", field(dune, "path")], check=True)
status, downloads = request("GET", "/ThemeSongs/downloads", token=token)
assert status == 200 and all(field(item, "name") != "Dune" for item in field(downloads, "items")), (
    f"externally edited theme remains managed: {downloads}"
)
subprocess.run(["docker", "compose", "-f", "tests/e2e/compose.yaml", "exec", "-T", "jellyfin", "test", "-f", field(dune, "path")], check=True)

removed = next((item for item in field(downloads, "items") if field(item, "kind") == "Series"), None)
assert removed is not None, f"no managed series to remove: {downloads}"
status, _ = request("DELETE", "/Items/" + field(removed, "itemId"), token=token)
assert status == 204, f"remove series from Jellyfin: {status}"
for _ in range(30):
    status, downloads = request("GET", "/ThemeSongs/downloads", token=token)
    if status == 200 and all(field(item, "itemId") != field(removed, "itemId") for item in field(downloads, "items")):
        break
    time.sleep(1)
assert status == 200 and all(field(item, "itemId") != field(removed, "itemId") for item in field(downloads, "items")), (
    f"removed series remains managed: {downloads}"
)
status, filtered = request("GET", "/ThemeSongs/downloads?search=no-such-theme", token=token)
assert status == 200 and field(filtered, "total") == 0 and field(filtered, "allTotal") > 0, f"bulk action must include filtered-out themes: {filtered}"
status, deleted = request("DELETE", "/ThemeSongs/downloads", token=token)
assert status == 200 and field(deleted, "deleted") == field(filtered, "allTotal") and field(deleted, "skipped") == 0, (
    f"delete all managed themes: {status} {deleted}"
)
status, downloads = request("GET", "/ThemeSongs/downloads", token=token)
assert status == 200 and field(downloads, "allTotal") == 0, f"managed themes remain after delete all: {downloads}"
subprocess.run(["docker", "compose", "-f", "tests/e2e/compose.yaml", "exec", "-T", "jellyfin", "cmp", "-s",
                "/tmp/user-theme-original", "/media/movies/User Theme (2000)/theme.mp3"], check=True)
subprocess.run(["docker", "compose", "-f", "tests/e2e/compose.yaml", "exec", "-T", "jellyfin", "cmp", "-s",
                "/tmp/edited-theme-original", field(dune, "path")], check=True)
print("Movie and series scan, refresh, delete, bulk delete, and stale-record cleanup passed")
