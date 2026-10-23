@echo off
setlocal
cd /d "%~dp0"
set "MISSING="
if not exist "AudioPane.cs" set "MISSING=AudioPane.cs"
if not exist "Branding.cs" set "MISSING=Branding.cs"
if not exist "ConsoleDialogs.cs" set "MISSING=ConsoleDialogs.cs"
if not exist "ConsoleModels.cs" set "MISSING=ConsoleModels.cs"
if not exist "ConsoleVerify.cs" set "MISSING=ConsoleVerify.cs"
if not exist "ConsoleWave.cs" set "MISSING=ConsoleWave.cs"
if not exist "ConsoleWindow.Live.cs" set "MISSING=ConsoleWindow.Live.cs"
if not exist "ConsoleWindow.Operations.cs" set "MISSING=ConsoleWindow.Operations.cs"
if not exist "ConsoleWindow.Tabs.cs" set "MISSING=ConsoleWindow.Tabs.cs"
if not exist "ConsoleWindow.cs" set "MISSING=ConsoleWindow.cs"
if not exist "Controls.cs" set "MISSING=Controls.cs"
if not exist "CpsImport.cs" set "MISSING=CpsImport.cs"
if not exist "Hotkeys.cs" set "MISSING=Hotkeys.cs"
if not exist "Footswitch.cs" set "MISSING=Footswitch.cs"
if not exist "Program.cs" set "MISSING=Program.cs"
if not exist "RadioResources.cs" set "MISSING=RadioResources.cs"
if not exist "ReceiveAudio.cs" set "MISSING=ReceiveAudio.cs"
if not exist "Recorder.cs" set "MISSING=Recorder.cs"
if not exist "Sb9600.cs" set "MISSING=Sb9600.cs"
if not exist "TrcAudio.cs" set "MISSING=TrcAudio.cs"
if not exist "UiTheme.cs" set "MISSING=UiTheme.cs"
if not exist "branding\app.ico" set "MISSING=branding\app.ico"
if not exist "branding\logo.png" set "MISSING=branding\logo.png"
if defined MISSING (
  echo.
  echo Source file "%MISSING%" was not found in:
  echo   %CD%
  echo.
  echo Extract the WHOLE zip first: right-click the zip, Properties, tick Unblock, OK,
  echo then right-click the zip, Extract All. Run this script from the extracted
  echo DispatchConsole folder - do not run it from inside the zip window.
  pause
  exit /b 1
)
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Windows .NET Framework 4.x compiler not found.
  pause
  exit /b 1
)
if not exist build mkdir build
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /win32icon:branding\app.ico /resource:branding\logo.png,RadioDispatchConsole.logo.png /resource:branding\app.ico,RadioDispatchConsole.app.ico /main:RadioDispatchConsole.Program /out:"build\DispatchConsole.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll AudioPane.cs Branding.cs ConsoleDialogs.cs ConsoleModels.cs ConsoleVerify.cs ConsoleWave.cs ConsoleWindow.Live.cs ConsoleWindow.Operations.cs ConsoleWindow.Tabs.cs ConsoleWindow.cs Controls.cs CpsImport.cs Footswitch.cs Hotkeys.cs Program.cs RadioResources.cs ReceiveAudio.cs Recorder.cs Sb9600.cs TrcAudio.cs UiTheme.cs
if errorlevel 1 (
  echo.
  echo BUILD FAILED. Copy the compiler errors above for troubleshooting.
  pause
  exit /b 1
)
if not exist "build\resources.map" copy /y "resources.map" "build\resources.map" >nul
if not exist "build\channels.map" copy /y "channels.map" "build\channels.map" >nul
if not exist "build\audio.settings" copy /y "audio.settings" "build\audio.settings" >nul
copy /y "HARDWARE_SETUP.md" "build\HARDWARE_SETUP.md" >nul
copy /y "OPERATOR_CARD.md" "build\OPERATOR_CARD.md" >nul
if not exist "build\branding" mkdir "build\branding"
copy /y "branding\logo.png" "build\branding\logo.png" >nul
copy /y "branding\app.ico" "build\branding\app.ico" >nul
if exist "branding\brand.ini" copy /y "branding\brand.ini" "build\branding\brand.ini" >nul
if not exist "build\cps" mkdir "build\cps"
copy /y "cps\*.csv" "build\cps\" >nul
start "Dispatch Console" "%~dp0build\DispatchConsole.exe"
