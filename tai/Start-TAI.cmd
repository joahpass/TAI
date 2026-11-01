@echo off
cd /d "%~dp0"
set TAI_OPEN=1
node server/index.mjs
pause
