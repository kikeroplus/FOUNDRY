@echo off
rem Build FOUNDRY with the built-in .NET Framework 4 compiler (no SDK needed)
setlocal
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set SRC=Program.cs Game.cs Enemies.cs Boss.cs Background.cs Gfx.cs Text.cs Sound.cs
set REFS=/r:System.Windows.Forms.dll /r:System.Drawing.dll

rem First build only: make Foundry.ico with the exe itself
if not exist Foundry.ico (
  "%FW%\csc.exe" /nologo /target:winexe /optimize+ /platform:x64 /out:Foundry.exe %REFS% %SRC%
  if errorlevel 1 goto fail
  "%~dp0Foundry.exe" --make-icon Foundry.ico
)

"%FW%\csc.exe" /nologo /target:winexe /optimize+ /platform:x64 /out:Foundry.exe /win32icon:Foundry.ico %REFS% %SRC%
if errorlevel 1 goto fail
echo BUILD OK: Foundry.exe
exit /b 0
:fail
echo BUILD FAILED
exit /b 1
