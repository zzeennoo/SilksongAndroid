@echo off
setlocal
if "%~1"=="" (
  echo Usage: Build-On-Windows.cmd "D:\path\to\Linux Silksong depot" [OpenGLES3] [ETC2]
  echo        Build-On-Windows.cmd "D:\path\to\Linux Silksong depot" TextureReport
  echo.
  echo   OpenGLES3      build the experimental OpenGL ES backend instead of Vulkan
  echo   ETC2           re-encode DXT textures as same-size ETC2 on the PC (opt-in)
  echo   TextureReport  only write pc-output\texture-report.json; builds nothing
  echo.
  echo You can also drag the depot folder onto this file.
  pause
  exit /b 2
)
set "GFX=Vulkan"
set "TEX=Native"
set "REPORT="
for %%A in (%2 %3) do (
  if /I "%%~A"=="OpenGLES3" set "GFX=OpenGLES3"
  if /I "%%~A"=="ETC2" set "TEX=ETC2"
  if /I "%%~A"=="TextureReport" set "REPORT=1"
  if /I not "%%~A"=="OpenGLES3" if /I not "%%~A"=="ETC2" if /I not "%%~A"=="TextureReport" if not "%%~A"=="" (
    echo Unknown option: %%~A
    echo Use OpenGLES3, ETC2, TextureReport, or no options for a Vulkan build with native textures.
    pause
    exit /b 2
  )
)
if defined REPORT (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-On-Windows.ps1" -Depot "%~1" -TextureReport
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-On-Windows.ps1" -Depot "%~1" -GraphicsApi %GFX% -TextureFormat %TEX%
)
if errorlevel 1 pause
