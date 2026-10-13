#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
rm -rf dist/linux-x64
mkdir -p dist/linux-x64
dotnet publish src/IPTVDownloader/IPTVDownloader.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -o dist/linux-x64 \
  /p:PublishSingleFile=true \
  /p:PublishTrimmed=false \
  /p:DebugType=None
cp src/IPTVDownloader/Assets/iptv-downloader.png dist/linux-x64/iptv-downloader.png
printf '\nBuild concluido em: %s/dist/linux-x64\n' "$ROOT"
