@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
 echo Precisa instalar o SDK .NET 8: https://dotnet.microsoft.com/download/dotnet/8.0
 start "" "https://dotnet.microsoft.com/download/dotnet/8.0"
 pause
 exit /b 1
)
if not exist "teste-pc-dados" mkdir "teste-pc-dados"
if not exist "teste-pc-downloads" mkdir "teste-pc-downloads"
set "XDG_DATA_HOME=%CD%\teste-pc-dados"
set "IPTV_DOWNLOAD_PARENT=%CD%\teste-pc-downloads"
set "IPTV_DOWNLOAD_ROOT_NAME=IPTV-Teste-PC"
set "ASPNETCORE_URLS=http://127.0.0.1:5090"
start "" "http://127.0.0.1:5090"
echo Interface: http://127.0.0.1:5090
 echo Mantenha esta janela aberta durante o teste.
dotnet run --project "Web\IPTVDownloader.Web.csproj"
pause
