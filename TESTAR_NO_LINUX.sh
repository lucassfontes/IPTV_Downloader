#!/usr/bin/env bash
set -u
cd "$(dirname "$(readlink -f "$0")")" || exit 1
if ! command -v dotnet >/dev/null 2>&1; then
  echo 'Instale o SDK .NET 8 para testar: https://dotnet.microsoft.com/download/dotnet/8.0'
  if command -v xdg-open >/dev/null 2>&1; then xdg-open 'https://dotnet.microsoft.com/download/dotnet/8.0' >/dev/null 2>&1 & fi
  read -r -p 'Pressione Enter para fechar... ' _
  exit 1
fi
mkdir -p teste-pc-dados teste-pc-downloads
export XDG_DATA_HOME="$PWD/teste-pc-dados"
export IPTV_DOWNLOAD_PARENT="$PWD/teste-pc-downloads"
export IPTV_DOWNLOAD_ROOT_NAME='IPTV-Teste-PC'
export ASPNETCORE_URLS='http://127.0.0.1:5090'
( sleep 5; if command -v xdg-open >/dev/null 2>&1; then xdg-open 'http://127.0.0.1:5090' >/dev/null 2>&1; fi ) &
echo 'Interface: http://127.0.0.1:5090'
echo 'Mantenha esta janela aberta durante o teste.'
dotnet run --project Web/IPTVDownloader.Web.csproj
read -r -p 'Pressione Enter para fechar... ' _
