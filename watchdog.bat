@echo off
:loop
"%~dp0AIVOICE-API.exe"
echo [%date% %time%] AIVOICE-API exited. Restarting...
goto loop
