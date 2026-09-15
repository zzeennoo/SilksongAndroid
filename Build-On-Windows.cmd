@echo off
setlocal
if "%~1"=="" (
  echo Usage: Build-On-Windows.cmd "D:\path\to\Linux Silksong depot"
  echo        Build-On-Windows.cmd "D:\path\to\Linux Silksong depot" OpenGLES3
  echo.
  echo You can also drag the depot folder onto this file.
  pause
  exit /b 2
)
if not "%~2"=="" if /I not "%~2"=="OpenGLES3" (
  echo Unknown graphics backend: %~2
  echo Use OpenGLES3 or leave the second argument empty for Vulkan.
  pause
  exit /b 2
)
if /I "%~2"=="OpenGLES3" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-On-Windows.ps1" -Depot "%~1" -GraphicsApi OpenGLES3
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-On-Windows.ps1" -Depot "%~1"
)
if errorlevel 1 pause
