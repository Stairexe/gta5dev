#!/usr/bin/env bash
# Vercel build: installs the .NET 8 SDK (not present on Vercel's build image), then builds the static site.
set -euo pipefail
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks | grep -q '^8\.'; then
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 8.0 --install-dir "$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
fi
dotnet --version
bash "$(dirname "$0")/build-static.sh"
