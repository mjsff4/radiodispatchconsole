@echo off
cd /d "%~dp0"
if not exist "build\DispatchConsole.exe" (
  call Build_And_Run.cmd
  exit /b
)
start "Dispatch Console" "%~dp0build\DispatchConsole.exe"
