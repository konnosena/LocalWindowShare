@echo off
setlocal

if "%WINDOW_SHARE_PORTAL_PORT%"=="" set "WINDOW_SHARE_PORTAL_PORT=48331"

set "APP_PATH=%~dp0window-share-portal\bin\Release\net10.0-windows\WindowSharePortal.exe"

if not exist "%APP_PATH%" (
    dotnet build "%~dp0window-share-portal\WindowSharePortal.csproj" --configuration Release --ignore-failed-sources
    if errorlevel 1 exit /b 1
)

start "" "%APP_PATH%"
