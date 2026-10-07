"""Exercise plugin discovery and the admin boundary in an actual Jellyfin 12 server."""

import os
import subprocess
import time
import uuid
from datetime import datetime
from setup import request, wizard, libraries

PLUGIN = "129e8a8b-87f1-48d3-802b-7dd151d72920"


def field(data, name):
    return data.get(name, data.get(name[0].upper() + name[1:]))


def add_until(token, item_id, youtube_url=None, check_scheduled=False):
    status, result = request("POST", f"/ThemeSongs/{item_id}/add", {"youTubeUrl": youtube_url}, token=token, timeout=10)
    assert status == 202, f"queue theme add: {status} {result}"
    status, error = request("POST", "/ThemeSongs/scan", token=token)
    assert status == 409 and field(error, "code") == "queueBusy", f"queued adds must block full scans: {status} {error}"
    if check_scheduled:
        _, tasks = request("GET", "/ScheduledTasks", token=token)
        task = next(task for task in tasks if field(task, "key") == "ThemeSongsRescan")
        previous = field(task, "lastExecutionResult")
        _, before = request("GET", "/ThemeSongs/scan", token=token)
        status, _ = request("POST", "/ScheduledTasks/Running/" + field(task, "id"), token=token)
        assert status in (202, 204), f"request scan through Jellyfin's scheduled-task API: {status}"
        for _ in range(30):
            _, current = request("GET", "/ScheduledTasks/" + field(task, "id"), token=token)
            if field(current, "state") == "Idle" and field(current, "lastExecutionResult") != previous:
                assert field(field(current, "lastExecutionResult"), "status") == "Failed", f"scheduled scan must refuse active queue work: {current}"
                _, after = request("GET", "/ThemeSongs/scan", token=token)
                assert field(after, "runId") == field(before, "runId"), "refused scheduled scan must not process media or reset scan state"
                break
            time.sleep(0.2)
        else:
            raise AssertionError("scheduled scan did not refuse active queue work")
    for _ in range(180):
        status, downloads = request("GET", "/ThemeSongs/downloads", token=token)
        theme = next((item for item in field(downloads, "items") if uuid.UUID(field(item, "itemId")) == uuid.UUID(item_id)), None)
        assert theme is not None, f"queued item missing from managed downloads: {downloads}"
        if not field(theme, "pending"):
            assert field(theme, "status") == "Active", f"queued theme not installed: {theme}"
            return theme
        assert field(theme, "processing"), f"queued theme add failed: {theme}"
        time.sleep(2)
    raise AssertionError(f"queued theme add did not finish: {theme}")


def reprocess_until(token, item_id, youtube_url=None):
    action = "edit" if youtube_url else "refresh"
    status, result = request("POST", f"/ThemeSongs/{item_id}/{action}", {"youTubeUrl": youtube_url} if youtube_url else None, token=token, timeout=10)
    assert status == 202, f"queue {action}: {status} {result}"
    status, error = request("POST", "/ThemeSongs/scan", token=token)
    assert status == 409 and field(error, "code") == "queueBusy", f"queued {action} must block full scans: {status} {error}"
    for _ in range(180):
        _, downloads = request("GET", "/ThemeSongs/downloads", token=token)
        theme = next(item for item in field(downloads, "items") if uuid.UUID(field(item, "itemId")) == uuid.UUID(item_id))
        if not field(theme, "processing"):
            assert field(theme, "result") in ("Replaced", "No replacement found", "Already themed"), f"queued {action} failed: {theme}"
            return {"Result": field(theme, "result")}
        time.sleep(2)
    raise AssertionError(f"queued {action} did not finish: {theme}")


def assert_settings(token, enabled, libraries, minimum=50, loudness=-30, scan_on_library_refresh=True):
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
    checked_scan_guards = False
    for attempt in range(300):
        status, progress = request("GET", "/ThemeSongs/scan", token=token)
        if status == 200 and field(progress, "running") and field(progress, "currentItem"):
            if not checked_scan_guards and expect_current:
                target = "/ThemeSongs/00000000-0000-0000-0000-000000000001"
                for method, path, body in [("POST", target + "/add", {}), ("POST", target + "/refresh", None),
                                           ("POST", target + "/edit", {"youTubeUrl": "https://youtu.be/aaaaaaaaaaa"}),
                                           ("DELETE", target, None), ("DELETE", target + "/pending", None),
                                           ("DELETE", "/ThemeSongs/downloads", None), ("POST", "/ThemeSongs/downloads/redownload", None)]:
                    action_status, error = request(method, path, body, token)
                    assert action_status == 409 and field(error, "code") == "availableAfterScan", f"scan must block {method} {path}: {action_status} {error}"
                checked_scan_guards = True
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
assert config["TargetLufs"] == -30, "new installs default to quieter themes"
assert config["PreferFranchiseThemes"] is False, "movie franchise preference defaults off"
status, settings = request("GET", "/ThemeSongs/settings", token=token)
assert status == 200, f"admin settings: {status}"
assert settings.get("downloaderAvailable", settings.get("DownloaderAvailable")) is True, "downloader release metadata missing"
assert field(settings, "downloaderError") is None and field(settings, "runtimeError") is None, "download tools have no initial error"
assert field(settings, "minimumMatchStrength") == 50, "admin settings expose the effective match strength"
assert field(settings, "targetLufs") == -30, "admin settings expose the effective loudness target"
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
    status, error = request("POST", "/ThemeSongs/00000000-0000-0000-0000-000000000001/add", {"youTubeUrl": url}, token)
    assert status == 400 and field(error, "code") == "invalidYouTubeUrl", f"custom or invalid add source rejected: {status} {error}"
status, error = request("POST", "/ThemeSongs/00000000-0000-0000-0000-000000000001/add", {}, token)
assert status == 409 and field(error, "code") == "themeUnavailable", f"add missing item: {status} {error}"
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
for method, path in [("GET", "/ThemeSongs/downloads"), ("DELETE", "/ThemeSongs/downloads"), ("POST", "/ThemeSongs/downloads/redownload"), ("GET", "/ThemeSongs/strings/en-us"), ("GET", "/ThemeSongs/activity"), ("POST", "/ThemeSongs/queue/cancel"), ("POST", "/ThemeSongs/scan"), ("POST", "/ThemeSongs/settings"), ("POST", "/ThemeSongs/downloader/retry"), ("POST", "/ThemeSongs/00000000-0000-0000-0000-000000000001/edit")]:
    status, _ = request(method, path)
    assert status in (401, 403), f"unauthorized {path}: {status}"
print("Jellyfin 12 plugin smoke checks passed")
for method, path in [("GET", "/ThemeSongs/items?search=Dune"), ("POST", "/ThemeSongs/00000000-0000-0000-0000-000000000001/add")]:
    status, _ = request(method, path)
    assert status in (401, 403), f"unauthorized {path}: {status}"
status, _ = request("DELETE", "/ThemeSongs/00000000-0000-0000-0000-000000000001/pending")
assert status in (401, 403), f"unauthorized failed-add dismissal: {status}"
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
status, results = request("GET", "/ThemeSongs/items?search=Dune", token=token)
assert status == 200 and any(field(item, "name") == "Dune" for item in results), f"search library content without themes: {status} {results}"
for search in ("User%20Theme", ""):
    status, results = request("GET", "/ThemeSongs/items?search=" + search, token=token)
    assert status == 200 and results == [], f"exclude existing themes and empty searches: {status} {results}"
status, results = request("GET", "/ThemeSongs/items?search=Unselected%20Example", token=token)
assert status == 200 and any(field(item, "name") == "Unselected Example" for item in results), f"search must include unselected libraries: {status} {results}"
status, _ = request("POST", "/ThemeSongs/settings", {"enabled": False, "scanOnLibraryRefresh": False, "libraries": []}, token)
status, results = request("GET", "/ThemeSongs/items?search=Dune", token=token)
assert status == 200 and any(field(item, "name") == "Dune" for item in results), f"manual search works with no automatic libraries: {status} {results}"
request("POST", "/ThemeSongs/settings", {"enabled": False, "scanOnLibraryRefresh": False, "libraries": library_ids}, token)
user_theme = next(item for item in items["Items"] if item["Name"] == "User Theme")
status, result = request("POST", f"/ThemeSongs/{user_theme['Id']}/add", {"youTubeUrl": "https://youtu.be/aaaaaaaaaaa"}, token)
assert status == 409 and field(result, "code") == "anotherTheme", f"add must protect existing user theme: {status} {result}"
status, _ = request("DELETE", f"/ThemeSongs/{user_theme['Id']}/pending", token=token)
assert status == 409, f"dismissal cannot remove a user theme: {status}"
subprocess.run(["docker", "compose", "-f", "tests/e2e/compose.yaml", "exec", "-T", "jellyfin", "cmp", "-s",
                "/tmp/user-theme-original", "/media/movies/User Theme (2000)/theme.mp3"], check=True)
print("Add-theme search, source validation, authorization, and existing-file protection passed")
_, activity_before = request("GET", "/ThemeSongs/activity", token=token)
last_activity_id = field(field(activity_before, "last"), "runId")
queue_ids = [item["Id"] for item in items["Items"] if item["Name"] in ("Dune", "Unselected Example")]
for item_id in queue_ids:
    status, result = request("POST", f"/ThemeSongs/{item_id}/add", {}, token)
    assert status == 202, f"queue cancellable work: {status} {result}"
    if item_id == queue_ids[0]:
        _, first = request("GET", "/ThemeSongs/activity", token=token)
        single = field(first, "current")
        assert field(single, "kind") == "queue" and field(single, "total") == 1, f"single jobs use the shared activity status: {first}"
_, active = request("GET", "/ThemeSongs/activity", token=token)
queue = field(active, "current")
assert field(queue, "kind") == "queue" and field(queue, "running") and field(queue, "total") == 2, f"activity must include the whole queue: {active}"
status, error = request("POST", "/ThemeSongs/scan", token=token)
assert status == 409 and field(error, "code") == "queueBusy", f"active queue must block scans: {status} {error}"
status, _ = request("POST", "/ThemeSongs/queue/cancel", token=token)
assert status == 202, f"cancel queue: {status}"
for _ in range(60):
    _, activity = request("GET", "/ThemeSongs/activity", token=token)
    if field(activity, "current") is None:
        break
    time.sleep(0.2)
else:
    raise AssertionError(f"queue did not cancel safely: {activity}")
cancelled = field(activity, "last")
assert field(cancelled, "cancelled") and field(cancelled, "finishedAt") and not field(cancelled, "cancelling"), f"retain cancelled queue summary: {activity}"
assert field(cancelled, "failed") == field(queue, "failed"), f"cancellation must not count as failure: {activity}"
assert not field(cancelled, "activeItems"), f"cancelled queue must clear active work: {activity}"
assert field(cancelled, "kind") == "queue" and field(cancelled, "runId") != last_activity_id, "queue completion replaces the scan as last activity"
_, downloads = request("GET", "/ThemeSongs/downloads", token=token)
assert not field(downloads, "processing"), f"cancelled jobs must release the shared queue: {downloads}"
request("POST", "/ThemeSongs/settings", {"enabled": False, "scanOnLibraryRefresh": False, "libraries": []}, token)
status, _ = request("POST", "/ThemeSongs/scan", token=token)
assert status == 202, f"scan after queue cancellation: {status}"
for _ in range(60):
    _, activity = request("GET", "/ThemeSongs/activity", token=token)
    last = field(activity, "last")
    if field(activity, "current") is None and field(last, "runId") != field(cancelled, "runId"):
        break
    time.sleep(0.2)
else:
    raise AssertionError(f"completed scan did not replace last activity: {activity}")
assert field(last, "kind") == "scan" and field(last, "total") == 0, f"scan and queue share last activity: {activity}"
request("POST", "/ThemeSongs/settings", {"enabled": False, "scanOnLibraryRefresh": False, "libraries": library_ids}, token)
print("Unified activity status, queue cancellation, and latest-completion summary passed")
if os.environ.get("LIVE_YOUTUBE") == "0":
    from redownload import check_redownload
    check_redownload(token, items["Items"], library_ids)
    raise SystemExit(0)
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
dune_item = next(item for item in items["Items"] if item["Name"] == "Dune")
with open("dist/universal/yt-dlp-version", encoding="utf-8") as release:
    binary = f"/config/plugins/configurations/jellyscore-yt-dlp/{release.read().strip()}/yt-dlp_linux"
with open("dist/universal/deno-version", encoding="utf-8") as release:
    runtime = f"/config/plugins/configurations/jellyscore-yt-dlp/{release.read().strip()}/deno"
for path in (binary, runtime):
    subprocess.run(["docker", "compose", "-f", "tests/e2e/compose.yaml", "exec", "-T", "jellyfin", "test", "!", "-e", path], check=True)
_, before_manual = request("GET", "/ThemeSongs/scan", token=token)
assert field(before_manual, "total") == 0, "no media scan has prepared the download tools"
add_until(token, dune_item["Id"], check_scheduled=True)
for path in (binary, runtime):
    subprocess.run(["docker", "compose", "-f", "tests/e2e/compose.yaml", "exec", "-T", "jellyfin", "test", "-x", path], check=True)
_, after_manual = request("GET", "/ThemeSongs/scan", token=token)
assert field(after_manual, "runId") == field(before_manual, "runId"), "first manual add installed tools without starting a scan"
print("First manual add installed yt-dlp and Deno without a media scan")
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
unselected_item = next(item for item in items["Items"] if item["Name"] == "Unselected Example")
unselected_theme = add_until(token, unselected_item["Id"], original_source)
assert field(unselected_theme, "source") == original_source, "manual add uses the chosen URL in an unselected library"
status, result = request("GET", "/ThemeSongs/downloads", token=token)
assert any(uuid.UUID(field(item, "itemId")) == uuid.UUID(unselected_item["Id"]) for item in field(result, "items")), "unselected-library themes remain managed after reconciliation"
status, _ = request("DELETE", f"/ThemeSongs/{unselected_item['Id']}", token=token)
assert status == 204, f"delete managed theme in unselected library: {status}"
assert field(theme, "score") > 0, theme
assert field(theme, "status") == "Active", theme
subprocess.run(["docker", "compose", "-f", "tests/e2e/compose.yaml", "exec", "-T", "jellyfin", "test", "-x", binary], check=True)
status, _ = request("POST", "/ThemeSongs/downloader/retry", token=token)
assert status == 204, f"retry endpoint should reuse the verified download: {status}"
status, songs = request("GET", f"/Items/{movie['Id']}/ThemeSongs", token=token)
assert status == 200 and songs.get("TotalRecordCount", 0) > 0, f"Jellyfin cannot see downloaded theme: {status} {songs}"
scan_until(token, "alreadyThemed")
status, downloads = request("GET", "/ThemeSongs/downloads", token=token)
assert status == 200, f"list before refresh: {status}"
original_order = [field(item, "itemId") for item in field(downloads, "items")]
original_date = field(next(item for item in field(downloads, "items") if uuid.UUID(field(item, "itemId")) == uuid.UUID(movie["Id"])), "date")
refreshed = reprocess_until(token, movie["Id"])
status, downloads = request("GET", "/ThemeSongs/downloads", token=token)
theme = next((item for item in field(downloads, "items") if uuid.UUID(field(item, "itemId")) == uuid.UUID(movie["Id"])), None)
assert status == 200 and theme is not None, f"refresh lost managed theme: {downloads}"
assert field(theme, "status") == "Active", downloads
assert [field(item, "itemId") for item in field(downloads, "items")] == original_order, "refresh changed table order"
if field(refreshed, "result") == "Replaced":
    assert datetime.fromisoformat(field(theme, "date")) > datetime.fromisoformat(original_date), "replacement did not update its displayed date"
refreshed_date = field(theme, "date")
edited = reprocess_until(token, movie["Id"], original_source)
assert field(edited, "result") == "Replaced", f"reprocess explicitly selected, previously excluded source: {edited}"
status, downloads = request("GET", "/ThemeSongs/downloads", token=token)
theme = next(item for item in field(downloads, "items") if uuid.UUID(field(item, "itemId")) == uuid.UUID(movie["Id"]))
assert field(theme, "source") == original_source and field(theme, "status") == "Active", f"edited source not installed: {theme}"
assert [field(item, "itemId") for item in field(downloads, "items")] == original_order, "editing the source changed table order"
assert datetime.fromisoformat(field(theme, "date")) > datetime.fromisoformat(refreshed_date), "editing the source did not update its displayed date"
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
add_until(token, movie["Id"], original_source)
status, results = request("GET", "/ThemeSongs/items?search=Sorcerer", token=token)
assert status == 200 and results == [], f"managed themes must be excluded from add search: {status} {results}"
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
status, _ = request("POST", f"/ThemeSongs/{unselected_item['Id']}/add", {"youTubeUrl": "https://youtu.be/aaaaaaaaaaa"}, token)
assert status == 202, f"queue unavailable source: {status}"
status, _ = request("DELETE", f"/ThemeSongs/{unselected_item['Id']}/pending", token=token)
assert status == 409, f"active processing cannot be dismissed: {status}"
for _ in range(180):
    _, downloads = request("GET", "/ThemeSongs/downloads", token=token)
    failed = next(item for item in field(downloads, "items") if uuid.UUID(field(item, "itemId")) == uuid.UUID(unselected_item["Id"]))
    if not field(failed, "processing"):
        break
    time.sleep(2)
else:
    raise AssertionError("unavailable source did not report failure")
assert field(failed, "pending") and field(failed, "code"), f"failed add must be dismissible: {failed}"
status, _ = request("POST", "/ThemeSongs/settings", {"enabled": False, "scanOnLibraryRefresh": False, "libraries": []}, token)
assert status == 204, f"select no libraries for the completed-failure scan check: {status}"
_, before_scan = request("GET", "/ThemeSongs/scan", token=token)
status, _ = request("POST", "/ThemeSongs/scan", token=token)
assert status == 202, f"completed failures must not block full scans: {status}"
for _ in range(10):
    _, progress = request("GET", "/ThemeSongs/scan", token=token)
    if field(progress, "runId") != field(before_scan, "runId") and not field(progress, "running"):
        break
    time.sleep(1)
else:
    raise AssertionError("empty-library scan did not finish after a completed failure")
status, _ = request("DELETE", f"/ThemeSongs/{unselected_item['Id']}/pending", token=token)
assert status == 204, f"dismiss failed add: {status}"
_, downloads = request("GET", "/ThemeSongs/downloads", token=token)
assert field(downloads, "allTotal") == 0, f"dismissed failure remains in table: {downloads}"
subprocess.run(["docker", "compose", "-f", "tests/e2e/compose.yaml", "exec", "-T", "jellyfin", "test", "!", "-e", field(unselected_theme, "path")], check=True)
print("Failed-add dismissal removes only the queue entry and refuses active work")
