@echo off
:loop
"%~dp0AIVOICE-API.exe"
echo [%date% %time%] AIVOICE-API exited.
set /a LOCKWAIT=0
:lockcheck
if not exist "%~dp0update.lock" goto restart
if %LOCKWAIT% geq 180 goto restart
%SystemRoot%\System32\ping.exe -n 2 127.0.0.1 >nul
set /a LOCKWAIT+=1
goto lockcheck
:restart
echo [%date% %time%] Restarting...
%SystemRoot%\System32\ping.exe -n 3 127.0.0.1 >nul
goto loop
