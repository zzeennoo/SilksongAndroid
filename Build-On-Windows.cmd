@echo off
setlocal
if "%~1"=="" (
  echo Usage: Build-On-Windows.cmd "D:\path\to\Linux Silksong depot"
  echo.
  echo You can also drag the depot folder onto this file.
  pause
  exit /b 2
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-On-Windows.ps1" -Depot "%~1"
if errorlevel 1 pause
