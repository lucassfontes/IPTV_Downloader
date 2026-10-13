#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
if ! command -v dotnet >/dev/null 2>&1; then
  echo "Erro: .NET 8 SDK não encontrado."
  echo "Instale o .NET 8 SDK e execute novamente."
  exit 1
fi
dotnet run --project src/IPTVDownloader/IPTVDownloader.csproj -c Release
