@echo off
setlocal
cd /d "%~dp0\.."
if exist dist\win-x64 rmdir /s /q dist\win-x64
mkdir dist\win-x64
dotnet publish src\IPTVDownloader\IPTVDownloader.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -o dist\win-x64 ^
  /p:PublishSingleFile=true ^
  /p:PublishTrimmed=false ^
  /p:DebugType=None
if errorlevel 1 exit /b 1
echo Build concluido em dist\win-x64
