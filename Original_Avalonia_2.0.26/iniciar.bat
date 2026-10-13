@echo off
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo Erro: .NET 8 SDK nao encontrado.
  pause
  exit /b 1
)
dotnet run --project src\IPTVDownloader\IPTVDownloader.csproj -c Release
