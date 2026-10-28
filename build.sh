#!/usr/bin/env bash
# Builds and tests the plugin. Requires the .NET 10 SDK.
# Output: dist/Jellyfin.Plugin.SearchAccelerator.dll
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

rm -rf dist
dotnet test --project tests/Jellyfin.Plugin.SearchAccelerator.Tests.csproj
dotnet publish src/Jellyfin.Plugin.SearchAccelerator.csproj --configuration Release --output dist

echo
echo "Built dist/Jellyfin.Plugin.SearchAccelerator.dll"
echo "Copy it to <jellyfin data>/plugins/SearchAccelerator_1.0.0.0/ and restart Jellyfin."
