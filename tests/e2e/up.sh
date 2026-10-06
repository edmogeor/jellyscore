#!/usr/bin/env bash
set -euo pipefail
bash package.sh
mkdir -p dist/e2e
# Jellyfin memory-maps plugin assemblies; stop it before replacing the mounted DLL.
docker compose -f tests/e2e/compose.yaml stop jellyfin
python3 -m zipfile -e dist/JellyScore.zip dist/e2e
docker compose -f tests/e2e/compose.yaml up -d --wait --wait-timeout 180
