@echo off
setlocal
title MyPlugin build
pushd "%~dp0"
if errorlevel 1 goto failed

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Pack.ps1"
set "buildExitCode=%errorlevel%"
popd
if not "%buildExitCode%"=="0" goto failed

color 2F
echo.
echo BUILD OK - artifacts and artifacts\LATEST updated.
pause
exit /b 0

:failed
color 4F
echo.
echo BUILD FAILED - do not use artifacts\LATEST from this attempt.
pause
exit /b 1
