"""Check bulk replacement with controlled source audio and real Jellyfin/FFmpeg."""

import hashlib
import json
import re
import subprocess
import time

from setup import field, request


def check_redownload(token, items, library_ids):
    def docker(*args, input=None):
        return subprocess.run(["docker", "compose", "-f", "tests/e2e/compose.yaml", "exec", "-T", "jellyfin", *args],
                              input=input, capture_output=True, check=True)

    def write(path, text):
        docker("sh", "-c", 'mkdir -p "$(dirname "$1")"; cat > "$1"', "sh", path, input=text.encode())

    def restart():
        subprocess.run(["docker", "compose", "-f", "tests/e2e/compose.yaml", "restart", "jellyfin"], check=True)
        for _ in range(60):
            if request("GET", "/ThemeSongs/settings", token=token)[0] == 200:
                return
            time.sleep(0.5)
        raise AssertionError("Jellyfin did not restart for redownload checks")

    def settings(target):
        status, result = request("POST", "/ThemeSongs/settings", {"enabled": False, "scanOnLibraryRefresh": False,
                                  "libraries": library_ids, "targetLufs": target}, token)
        assert status == 204, f"save audio settings: {status} {result}"

    def finish_run(previous):
        for _ in range(300):
            _, activity = request("GET", "/ThemeSongs/activity", token=token)
            last = field(activity, "last")
            if field(activity, "current") is None and field(last, "runId") != previous:
                return last
            time.sleep(0.2)
        raise AssertionError(f"redownload run did not finish: {activity}")

    def state():
        return json.loads(docker("cat", "/config/plugins/configurations/theme-songs-state.json").stdout)

    def start_bulk(expected):
        _, activity = request("GET", "/ThemeSongs/activity", token=token)
        previous = field(field(activity, "last"), "runId")
        status, result = request("POST", "/ThemeSongs/downloads/redownload", token=token)
        assert status == 202 and field(result, "queued") == expected, f"queue all managed themes: {status} {result}"
        return previous

    def retry_issue(issue):
        _, activity = request("GET", "/ThemeSongs/activity", token=token)
        previous = field(field(activity, "last"), "runId")
        assert field(issue, "retryable") and "RetryJob" not in issue, f"issue must expose recovery without private request data: {issue}"
        status, result = request("POST", f"/ThemeSongs/issues/{field(issue, 'id')}/retry", token=token)
        assert status == 202, f"retry only the failed item: {status} {result}"
        status, result = request("POST", f"/ThemeSongs/issues/{field(issue, 'id')}/retry", token=token)
        assert status == 409 and field(result, "code") == "queueBusy", f"duplicate retry must not enqueue twice: {status} {result}"
        run = finish_run(previous)
        assert field(run, "total") == 1 and field(run, "failed") == 0, f"retry must repeat a single operation: {run}"
        status, result = request("POST", f"/ThemeSongs/issues/{field(issue, 'id')}/retry", token=token)
        assert status == 404 and field(result, "code") == "issueUnavailable", "old issues cannot be retried after their run is replaced"
        return run

    _, initial = request("GET", "/ThemeSongs/downloads", token=token)
    assert field(initial, "managedTotal") == 0, "controlled redownload checks require an empty managed-theme fixture"

    checksum_path = "/config/plugins/theme-songs/SHA2-256SUMS"
    version_path = "/config/plugins/theme-songs/yt-dlp-version"
    checksums = docker("cat", checksum_path).stdout.decode()
    version = docker("cat", version_path).stdout.decode()
    fake_folder = "/config/plugins/configurations/jellyscore-yt-dlp/bulk-redownload-test"
    # Exercise the same checksum verification and duration checks without depending on YouTube availability.
    downloader = '''#!/bin/sh
output=; previous=; metadata=; id=
for arg in "$@"; do
    [ "$previous" = "-o" ] && output="$arg"
    case "$arg" in --dump-json) metadata=1 ;; ytsearch*) id=ccccccccccc ;; https://www.youtube.com/watch?v=*) id="${arg##*=}" ;; esac
    previous="$arg"
done
printf '%s\\n' "$id" >> /tmp/redownload-requests
if [ -e /tmp/redownload-refresh-fail ] || { [ -e /tmp/redownload-fail ] && [ "$id" = aaaaaaaaaaa ]; }; then
    printf 'Controlled source unavailable\\n' >&2; exit 1
fi
if [ "$metadata" = 1 ]; then
    title='Original recording'
    [ "$id" = ccccccccccc ] && title='Dune 2021 Main theme'
    printf '{"id":"%s","title":"%s","duration":20,"description":"","channel":"Test"}\\n' "$id" "$title"
else
    cp /tmp/redownload-raw.wav "$output"
fi
'''
    deno = "/usr/local/bin/deno"
    had_deno = docker("sh", "-c", '[ -e "$1" ] && printf yes || printf no', "sh", deno).stdout == b"yes"
    if had_deno:
        docker("cp", deno, "/tmp/redownload-deno-original")
    theme_ids = [item["Id"] for item in items if item["Name"] in ("Dune", "Unselected Example")]
    created = {}
    try:
        write(version_path, "bulk-redownload-test\n")
        write(checksum_path, hashlib.sha256(downloader.encode()).hexdigest() + "  yt-dlp_linux\n")
        write(fake_folder + "/yt-dlp_linux", downloader)
        write(deno, "#!/bin/sh\nprintf 'deno 2.3.0\\n'\n")
        docker("chmod", "+x", deno, fake_folder + "/yt-dlp_linux")
        docker("/usr/lib/jellyfin-ffmpeg/ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi",
               "-i", "sine=frequency=440:duration=20", "/tmp/redownload-raw.wav")
        restart()
        settings(-30)
        for item_id, video_id in zip(theme_ids, ("aaaaaaaaaaa", "bbbbbbbbbbb")):
            _, activity = request("GET", "/ThemeSongs/activity", token=token)
            previous = field(field(activity, "last"), "runId")
            status, result = request("POST", f"/ThemeSongs/{item_id}/add", {"youTubeUrl": "https://www.youtube.com/watch?v=" + video_id}, token)
            assert status == 202, f"install controlled managed theme: {status} {result}"
            run = finish_run(previous)
            assert field(run, "added") == 1 and field(run, "failed") == 0, f"controlled add failed: {run}"
            created = dict(state()["Themes"])
        before = state()
        created = dict(before["Themes"])
        settings(-35)
        previous = start_bulk(2)
        status, _ = request("POST", "/ThemeSongs/queue/cancel", token=token)
        assert status == 202, "bulk redownload uses shared activity cancellation"
        cancelled = finish_run(previous)
        assert field(cancelled, "cancelled") and field(cancelled, "failed") == 0 and field(cancelled, "processed") < 2, f"cancellation did not discard waiting bulk work safely: {cancelled}"
        before = state()
        previous = start_bulk(2)
        status, error = request("POST", "/ThemeSongs/downloads/redownload", token=token)
        assert status == 409 and field(error, "code") == "queueBusy", "a running batch must reject duplicate bulk submissions"
        settings(-40)
        run = finish_run(previous)
        assert field(run, "updated") == 2 and field(run, "failed") == 0, f"bulk redownload failed: {run}"
        after = state()
        for item_id, record in before["Themes"].items():
            updated = after["Themes"][item_id]
            for key in ("VideoId", "SourceUrl", "Recording", "Score", "Evidence", "VideoTitle", "AddedAt"):
                assert updated[key] == record[key], f"redownload changed {key}: {updated}"
            assert updated["Date"] > record["Date"], "redownload updates the displayed date"
            analysis = docker("/usr/lib/jellyfin-ffmpeg/ffmpeg", "-hide_banner", "-nostats", "-i", updated["Path"],
                              "-af", "ebur128=peak=true:framelog=verbose", "-f", "null", "-").stderr.decode()
            loudness = float(re.search(r"Integrated loudness:\s+I:\s+(-?\d+(?:\.\d+)?) LUFS", analysis)[1])
            assert abs(loudness + 35) < 1, f"batch did not retain its accepted audio settings: {loudness}"
        assert after["ExcludedVideos"] == before["ExcludedVideos"] and after["ExcludedRecordings"] == before["ExcludedRecordings"], "redownload must not exclude the current sources or recordings"

        write("/tmp/redownload-fail", "fail")
        previous = start_bulk(2)
        run = finish_run(previous)
        assert field(run, "failed") == 1 and field(run, "updated") == 1, f"per-item failures must not stop the rest of the batch: {run}"
        failed_id = next(item_id for item_id, record in after["Themes"].items() if record["VideoId"] == "aaaaaaaaaaa")
        assert state()["Themes"][failed_id] == after["Themes"][failed_id], "failed replacement changed the existing theme or its record"
        assert docker("sha256sum", after["Themes"][failed_id]["Path"]).stdout.decode().split()[0].upper() == after["Themes"][failed_id]["Hash"], "failed download changed the existing file"
        docker("rm", "/tmp/redownload-fail")
        issue = next(issue for issue in field(run, "issues") if field(issue, "failed"))
        recovered = retry_issue(issue)
        assert field(recovered, "updated") == 1 and state()["Themes"][failed_id]["VideoId"] == "aaaaaaaaaaa", "redownload retry must keep its saved source"
        assert state()["ExcludedVideos"] == before["ExcludedVideos"] and state()["ExcludedRecordings"] == before["ExcludedRecordings"], "redownload retries do not become refreshes"

        changed = next(record for record in created.values() if record["VideoId"] == "bbbbbbbbbbb")
        docker("sh", "-c", 'printf external-edit >> "$1"', "sh", changed["Path"])
        changed_hash = docker("sha256sum", changed["Path"]).stdout
        write("/tmp/redownload-requests", "")
        run = finish_run(start_bulk(1))
        assert field(run, "updated") == 1 and field(run, "failed") == 0, f"eligible managed themes should still redownload: {run}"
        assert docker("sha256sum", changed["Path"]).stdout == changed_hash, "redownload overwrote an externally modified file"
        assert set(docker("cat", "/tmp/redownload-requests").stdout.decode().splitlines()) == {"aaaaaaaaaaa"}, "bulk redownload searched for another recording or included an unmanaged theme"
        docker("cmp", "-s", "/tmp/user-theme-original", "/media/movies/User Theme (2000)/theme.mp3")

        fresh_item = next(item for item in items if "Sorcerer" in item["Name"])
        write("/tmp/redownload-fail", "fail")
        _, activity = request("GET", "/ThemeSongs/activity", token=token)
        previous = field(field(activity, "last"), "runId")
        status, _ = request("POST", f"/ThemeSongs/{fresh_item['Id']}/add", {"youTubeUrl": "https://www.youtube.com/watch?v=aaaaaaaaaaa"}, token)
        assert status == 202, "queue a controlled unsuccessful add"
        run = finish_run(previous)
        _, downloads = request("GET", "/ThemeSongs/downloads", token=token)
        assert all(field(row, "itemId").replace("-", "") != fresh_item["Id"].replace("-", "") for row in field(downloads, "items")), "an unsuccessful add does not create a card"
        _, eligible = request("GET", "/ThemeSongs/items?search=Sorcerer", token=token)
        assert any(field(item, "itemId").replace("-", "") == fresh_item["Id"].replace("-", "") for item in eligible), "unsuccessful adds remain searchable"
        docker("rm", "/tmp/redownload-fail")
        assert field(retry_issue(field(run, "issues")[0]), "added") == 1, "add retries retain the selected YouTube source"
        created.update(state()["Themes"])

        write("/tmp/redownload-refresh-fail", "fail")
        _, activity = request("GET", "/ThemeSongs/activity", token=token)
        previous = field(field(activity, "last"), "runId")
        status, _ = request("POST", f"/ThemeSongs/{failed_id}/refresh", token=token)
        assert status == 202, "queue a controlled failed refresh"
        run = finish_run(previous)
        docker("rm", "/tmp/redownload-refresh-fail")
        assert field(retry_issue(field(run, "issues")[0]), "updated") == 1, "refresh retries replace only the failed item"
        assert state()["Themes"][failed_id]["VideoId"] == "ccccccccccc", "refresh retry searches for another recording"

        write("/tmp/redownload-fail", "fail")
        _, activity = request("GET", "/ThemeSongs/activity", token=token)
        previous = field(field(activity, "last"), "runId")
        status, _ = request("POST", f"/ThemeSongs/{failed_id}/edit", {"youTubeUrl": "https://www.youtube.com/watch?v=aaaaaaaaaaa"}, token)
        assert status == 202, "queue a controlled failed source edit"
        run = finish_run(previous)
        docker("rm", "/tmp/redownload-fail")
        assert field(retry_issue(field(run, "issues")[0]), "updated") == 1, "source-edit retries replace only the failed item"
        assert state()["Themes"][failed_id]["VideoId"] == "aaaaaaaaaaa", "source-edit retry preserves the selected URL"
        print("Activity retries preserve add, refresh, source-edit, and redownload semantics; failed adds stay outside downloads")
        print("Saved-source bulk redownload, captured audio settings, failure recovery, and ownership checks passed")
    finally:
        request("POST", "/ThemeSongs/queue/cancel", token=token)
        for item_id, record in created.items():
            request("DELETE", f"/ThemeSongs/{item_id}", token=token)
            # The second file was deliberately changed by this fixture and is no longer managed.
            docker("rm", "-f", record["Path"])
        settings(-30)
        write(checksum_path, checksums)
        write(version_path, version)
        if had_deno:
            docker("cp", "/tmp/redownload-deno-original", deno)
        else:
            docker("rm", "-f", deno)
        docker("rm", "-rf", fake_folder)
        docker("rm", "-f", "/tmp/redownload-fail", "/tmp/redownload-refresh-fail", "/tmp/redownload-raw.wav", "/tmp/redownload-requests", "/tmp/redownload-deno-original")
        restart()
