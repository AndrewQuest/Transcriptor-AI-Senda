#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
sdk_root=/workspace/.tools/dotnet
sdk_version="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$repo_root/global.json")"
mkdir -p "$sdk_root"
if [[ ! -x "$sdk_root/dotnet" ]] || [[ "$("$sdk_root/dotnet" --list-sdks)" != *"$sdk_version "* ]]; then
  installer="$(mktemp /tmp/senda-dotnet-XXXXXX.sh)"
  trap 'rm -f "$installer"' EXIT
  curl --fail --silent --show-error --location https://dot.net/v1/dotnet-install.sh -o "$installer"
  bash "$installer" --version "$sdk_version" --install-dir "$sdk_root" --no-path
fi
export DOTNET_ROOT="$sdk_root"
export PATH="$sdk_root:$PATH"
export DOTNET_CLI_HOME=/workspace/.tools/dotnet-home
export NUGET_PACKAGES=/workspace/.tools/nuget
export NUGET_HTTP_CACHE_PATH=/workspace/.tools/nuget-http
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$repo_root"
dotnet restore Transcriptor.sln --locked-mode
dotnet build Transcriptor.sln -c Release --no-restore
dotnet test tests/Transcriptor.Tests/Transcriptor.Tests.csproj -c Release --no-build
