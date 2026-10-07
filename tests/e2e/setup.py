"""Shared Jellyfin setup for the local preview and live test."""

import json
import subprocess
import time
import urllib.parse


LIBRARIES = (("Films", "movies", "/media/movies"),
             ("Shows", "tvshows", "/media/shows"),
             ("Other films", "movies", "/media/unused"))


def field(data, name):
    return data.get(name, data.get(name[0].upper() + name[1:]))


def request(method, path, body=None, token=None, timeout=5):
    auth = f'MediaBrowser Token="{token}"' if token else 'MediaBrowser Client="e2e", Device="e2e", DeviceId="e2e", Version="1"'
    command = ["docker", "compose", "-f", "tests/e2e/compose.yaml", "exec", "-T", "jellyfin",
               "curl", "--silent", "--show-error", "--max-time", str(timeout), "--write-out", "\n%{http_code}",
               "--request", method, "--header", "Content-Type: application/json", "--header", f"Authorization: {auth}"]
    if body is not None:
        command += ["--data-binary", "@-"]
    command.append("http://127.0.0.1:8096" + path)
    try:
        result = subprocess.run(command, input=json.dumps(body).encode() if body is not None else None,
                                capture_output=True, timeout=timeout + 5, check=True)
        content, _, status = result.stdout.rpartition(b"\n")
        return int(status), json.loads(content or b"null")
    except (subprocess.CalledProcessError, subprocess.TimeoutExpired, ValueError):
        return 503, None


def wizard():
    for _ in range(60):
        status, info = request("GET", "/System/Info/Public")
        if status == 200 and isinstance(info, dict) and "StartupWizardCompleted" in info:
            break
        time.sleep(2)
    else:
        raise RuntimeError("Jellyfin API did not become ready")
    if info["StartupWizardCompleted"]:
        return
    for method, endpoint, body in [
        ("POST", "/Startup/Configuration", {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"}),
        ("GET", "/Startup/User", None),
        ("POST", "/Startup/User", {"Name": "user", "Password": "password"}),
        ("POST", "/Startup/RemoteAccess", {"EnableRemoteAccess": True, "EnableAutomaticPortMapping": False}),
        ("POST", "/Startup/Complete", None),
    ]:
        for _ in range(30):
            status, _ = request(method, endpoint, body)
            if status in (200, 204):
                break
            time.sleep(2)
        if status not in (200, 204):
            raise RuntimeError(f"Jellyfin setup failed at {endpoint}: HTTP {status}")


def libraries(token):
    status, existing = request("GET", "/Library/VirtualFolders", token=token)
    if status != 200:
        raise RuntimeError(f"Could not list libraries: HTTP {status}")
    for name, kind, media_path in LIBRARIES:
        if any(folder["Name"] == name for folder in existing):
            continue
        path = "/Library/VirtualFolders?" + urllib.parse.urlencode(
            {"name": name, "collectionType": kind, "paths": media_path, "refreshLibrary": "false"}
        )
        status, _ = request("POST", path, {}, token)
        if status != 204:
            raise RuntimeError(f"Could not create {name} library: HTTP {status}")
    status, existing = request("GET", "/Library/VirtualFolders", token=token)
    if status != 200:
        raise RuntimeError(f"Could not list libraries: HTTP {status}")
    return {folder["Name"]: folder["ItemId"] for folder in existing}
