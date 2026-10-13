@echo off
cd /d "%~dp0Web"
where dotnet >nul 2>&1
if errorlevel 1 (
  echo Instale o .NET SDK 8: https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)
echo Abrir: http://localhost:5078
dotnet run --project IPTVDownloader.Web.csproj --urls http://127.0.0.1:5078
pause
