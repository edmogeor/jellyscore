.DEFAULT_GOAL := help
.PHONY: help setup format check test test-unit test-e2e test-smoke up package

help:
	@printf '%s\n' 'make setup      Restore .NET and Node tooling.' 'make format     Format C# and admin page.' 'make check      Verify formatting, linting, and Jellyfin 12 builds.' 'make test-unit  Run matcher and audio checks.' 'make test-e2e   Download a real theme in Jellyfin 12.' 'make test-smoke Run Jellyfin API checks without YouTube.' 'make test       Run unit and live e2e checks.' 'make up         Start a ready-to-scan Jellyfin 12 preview.' 'make package    Package the plugin and yt-dlp release metadata.'

setup:
	dotnet tool restore
	npm ci

format:
	dotnet tool restore
	dotnet csharpier format
	npm run format

check:
	dotnet tool restore
	dotnet build Jellyfin.Plugin.JellyScore/Jellyfin.Plugin.JellyScore.csproj -p:JellyfinVersion=12.0.0
	dotnet build Jellyfin.Plugin.JellyScore/Jellyfin.Plugin.JellyScore.csproj -p:JellyfinVersion=12.1.0
	dotnet csharpier check
	npm run format:check
	npm run lint
	npm run check:strings

test-unit:
	dotnet run --project checks/checks.csproj

test-e2e:
	bash tests/e2e/test.sh

test-smoke:
	LIVE_YOUTUBE=0 bash tests/e2e/test.sh

test: test-unit test-e2e

package:
	bash package.sh

up:
	bash tests/e2e/up.sh
	bash tests/e2e/fixtures.sh
	python3 tests/e2e/preview.py
