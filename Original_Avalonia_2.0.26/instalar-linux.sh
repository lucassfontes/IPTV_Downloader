#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "Erro: .NET 8 SDK não encontrado."
  echo "Instale o .NET 8 SDK e execute novamente."
  exit 1
fi

APP_DIR="$HOME/.local/share/iptv-downloader"
ICON_DIR="$HOME/.local/share/icons/hicolor/512x512/apps"
DESKTOP_DIR="$HOME/.local/share/applications"
BIN_DIR="$HOME/.local/bin"

./scripts/build-linux.sh

mkdir -p "$APP_DIR" "$ICON_DIR" "$DESKTOP_DIR" "$BIN_DIR"
rm -rf "$APP_DIR"/*
cp -a dist/linux-x64/. "$APP_DIR/"
cp src/IPTVDownloader/Assets/iptv-downloader.png "$ICON_DIR/iptv-downloader.png"

cat > "$DESKTOP_DIR/iptv-downloader.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=IPTV Downloader
Comment=Downloader de filmes e séries IPTV
Exec=$APP_DIR/IPTVDownloader
Icon=iptv-downloader
Terminal=false
Categories=AudioVideo;Network;
StartupNotify=true
StartupWMClass=IPTVDownloader
EOF

chmod +x "$DESKTOP_DIR/iptv-downloader.desktop" "$APP_DIR/IPTVDownloader"
ln -sfn "$APP_DIR/IPTVDownloader" "$BIN_DIR/iptv-downloader"

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$DESKTOP_DIR" >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache -f -t "$HOME/.local/share/icons/hicolor" >/dev/null 2>&1 || true
fi

echo
echo "IPTV Downloader instalado no menu de aplicativos."
echo "Ícone instalado: $ICON_DIR/iptv-downloader.png"
echo "Executável: $APP_DIR/IPTVDownloader"
echo "Atalho de terminal: iptv-downloader"
