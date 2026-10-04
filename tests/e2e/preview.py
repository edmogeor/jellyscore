"""Prepare a repeatable local JellyScore admin preview without YouTube requests."""

import time
from setup import request, wizard, libraries


wizard()
status, login = request("POST", "/Users/AuthenticateByName", {"Username": "user", "Pw": "password"})
if status != 200:
    raise RuntimeError(f"Preview admin login failed: HTTP {status}")
token = login["AccessToken"]
status, _ = request("POST", "/ThemeSongs/settings", {"enabled": False, "scanOnLibraryRefresh": False, "libraries": []}, token)
if status != 204:
    raise RuntimeError(f"Could not disable automatic processing: HTTP {status}")
folders = libraries(token)
status, _ = request("POST", "/ThemeSongs/settings", {"enabled": False, "scanOnLibraryRefresh": False,
                                                     "libraries": [folders["Films"], folders["Shows"]]}, token)
if status != 204:
    raise RuntimeError(f"Could not select preview libraries: HTTP {status}")
status, _ = request("POST", "/Library/Refresh", token=token)
if status not in (200, 204):
    raise RuntimeError(f"Could not index preview media: HTTP {status}")
for attempt in range(60):
    status, items = request("GET", "/Items?Recursive=true&IncludeItemTypes=Movie,Series", token=token)
    if status != 200:
        time.sleep(2)
        continue
    indexed = {item["Name"] for item in items["Items"]}
    if {"Dune", "Harry Potter and the Sorcerer's Stone", "User Theme", "Star Trek: The Next Generation"} <= indexed:
        break
    if attempt % 5 == 0:
        print(f"Waiting for preview media to be indexed ({len(items['Items'])} items)...", flush=True)
    time.sleep(2)
else:
    raise RuntimeError("Preview media was not indexed by Jellyfin")

print("Preview ready at http://127.0.0.1:18096/web/ (user / password). Open Dashboard > Plugins > JellyScore and click Scan libraries.")
