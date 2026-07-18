@echo off
setlocal
set "PID=%~1"
set "SRC=%~2"
set "DST=%~3"
set "EXE=%~4"
for %%f in ("%EXE%") do set "EXENAME=%%~nxf"
set "LOG=%~dp0update.log"
set "TASKLIST=%SystemRoot%\System32\tasklist.exe"
set "FINDEXE=%SystemRoot%\System32\find.exe"
set "PING=%SystemRoot%\System32\ping.exe"
set "ROBOCOPY=%SystemRoot%\System32\Robocopy.exe"

echo [%date% %time%] Update started. PID=%PID% >> "%LOG%"
echo   SRC=%SRC% >> "%LOG%"
echo   DST=%DST% >> "%LOG%"

set /a WAITED=0
:wait_exit
"%TASKLIST%" /FI "PID eq %PID%" /NH 2>nul | "%FINDEXE%" " %PID% " >nul
if errorlevel 1 goto stopped
if %WAITED% geq 60 goto stopped
"%PING%" -n 2 127.0.0.1 >nul
set /a WAITED+=1
goto wait_exit

:stopped
"%PING%" -n 2 127.0.0.1 >nul
echo [%date% %time%] Server stopped. Copying files... >> "%LOG%"

"%ROBOCOPY%" "%SRC%" "%DST%" /E /R:10 /W:1 /XF appsettings.json update.lock >> "%LOG%" 2>&1
if %errorlevel% geq 8 goto copy_failed

del "%DST%\update.lock" >nul 2>&1
echo [%date% %time%] Copy done. Waiting for restart... >> "%LOG%"

set /a WAITED=0
:wait_start
"%PING%" -n 2 127.0.0.1 >nul
"%TASKLIST%" /FI "IMAGENAME eq %EXENAME%" /NH 2>nul | "%FINDEXE%" /i "%EXENAME%" >nul
if not errorlevel 1 goto started_by_watchdog
set /a WAITED+=1
if %WAITED% lss 10 goto wait_start

echo [%date% %time%] Watchdog not detected. Starting server directly... >> "%LOG%"
start "" /d "%DST%" "%DST%\%EXENAME%"
echo [%date% %time%] Update complete. >> "%LOG%"
exit /b 0

:started_by_watchdog
echo [%date% %time%] Server restarted by watchdog. Update complete. >> "%LOG%"
exit /b 0

:copy_failed
echo [%date% %time%] COPY FAILED. Restarting previous version... >> "%LOG%"
del "%DST%\update.lock" >nul 2>&1
start "" /d "%DST%" "%DST%\%EXENAME%"
exit /b 1
