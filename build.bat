@echo off
cd /d "%~dp0"
dotnet publish -c Release -o publish
if errorlevel 1 exit /b 1

for /f "delims=" %%v in ('powershell -NoProfile -Command "(Get-Item 'publish\AIVOICE-API.exe').VersionInfo.ProductVersion"') do set "VER=%%v"
powershell -NoProfile -Command "Compress-Archive -Path publish\* -DestinationPath 'A.I.VOICE-API_v%VER%.zip' -Force"

echo.
echo Build complete: publish\AIVOICE-API.exe
echo Watchdog:        publish\watchdog.bat
echo Release zip:     A.I.VOICE-API_v%VER%.zip
