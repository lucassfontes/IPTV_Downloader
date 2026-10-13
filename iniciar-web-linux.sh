#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/Web"
if ! command -v dotnet >/dev/null 2>&1; then
  echo 'ERRO: instale o .NET SDK 8 ou superior para compilar e executar.'
  echo 'Site oficial: https://dotnet.microsoft.com/download/dotnet/8.0'
  exit 1
fi
echo 'IPTV Downloader Web: http://localhost:5078'
echo 'Feche este terminal ou pressione Ctrl+C para encerrar.'
dotnet run --project IPTVDownloader.Web.csproj --urls 'http://127.0.0.1:5078'
