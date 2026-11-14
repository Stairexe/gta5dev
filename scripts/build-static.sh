#!/usr/bin/env bash
# Builds the fully static site (front end + WebAssembly engine) into web/dist.
# The output needs no server: every file is built in the visitor's browser.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"

dotnet publish "$root/engine/GtaForge.Wasm" -c Release -o "$root/build/wasm"
rm -rf "$root/web/public/engine"
mkdir -p "$root/web/public/engine"
cp -r "$root/build/wasm/wwwroot/_framework" "$root/web/public/engine/"

cd "$root/web"
npm ci
npx tsc -b
npx vite build --outDir dist --emptyOutDir
echo "Static site ready in web/dist"
