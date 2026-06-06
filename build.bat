@echo off
cd /d "%~dp0"
dotnet publish -c Release -o publish
echo.
echo Build complete: publish\AIVOICE-API.exe
echo Watchdog:        publish\watchdog.bat
