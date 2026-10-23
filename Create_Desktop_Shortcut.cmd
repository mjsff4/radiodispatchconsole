@echo off
rem Creates (or updates) the "Dispatch Console" shortcut on the desktop for the console in this folder.
rem Run it again from each new version folder: the shortcut then points at the new version.
setlocal
set "EXE=%~dp0build\DispatchConsole.exe"
if not exist "%EXE%" (
  echo The console is not built yet. Run Build_And_Run.cmd in this folder first.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s = (New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop') + '\Dispatch Console.lnk'); $s.TargetPath = $env:EXE; $s.WorkingDirectory = [IO.Path]::GetDirectoryName($env:EXE); $s.IconLocation = $env:EXE + ',0'; $s.Description = 'Radio Dispatch Console'; $s.Save()"
if errorlevel 1 (
  echo Could not create the shortcut. Right-click build\DispatchConsole.exe ^> Send to ^> Desktop instead.
  pause
  exit /b 1
)
echo Desktop shortcut "Dispatch Console" now starts:
echo   %EXE%
pause
