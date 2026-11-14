@echo off
rem ============================================================================
rem  TmLab: clean build for Windows, then LAUNCH the app.
rem  Cleans bin and obj, restores packages, builds the Windows target,
rem  saves the full log to maui\build-log.txt and starts the app.
rem
rem  Usage: double-click this file, or run it from the command line:
rem      C:\...\memTransaction\maui\build-windows.cmd
rem
rem  (This file is ASCII-only on purpose: cmd.exe mangles Cyrillic text.)
rem ============================================================================

setlocal
set "ROOT=%~dp0"
cd /d "%ROOT%TmLab" || (echo [ERROR] Project folder not found: %ROOT%TmLab & pause & exit /b 1)

echo ==== 1/5  what is installed ====
echo.
echo -- dotnet --version --
call dotnet --version
echo.
echo -- dotnet --list-sdks --
call dotnet --list-sdks
echo.
echo -- dotnet workload list --
call dotnet workload list
echo.

echo ==== 2/5  removing bin and obj ====
if exist "bin" rmdir /s /q "bin"
if exist "obj" rmdir /s /q "obj"
if exist "bin" (echo [WARN] bin is still there - close Visual Studio and retry) else (echo cleaned)

echo.
echo ==== 3/5  restore ====
echo (this step needs access to nuget.org)
call dotnet restore > "%ROOT%build-log.txt" 2>&1
if errorlevel 1 goto failed
echo restore: OK

echo.
echo ==== 4/5  build ====
call dotnet build -f net10.0-windows10.0.19041.0 --no-restore >> "%ROOT%build-log.txt" 2>&1
if errorlevel 1 goto failed
echo build: OK

echo.
echo ==== 5/5  launching the app ====
set "EXE=bin\Debug\net10.0-windows10.0.19041.0\win-x64\TmLab.exe"
if not exist "%EXE%" (
  echo [ERROR] Executable not found: %EXE%
  echo Built executables:
  dir /s /b "bin\*.exe"
  exit /b 1
)
echo Starting: %EXE%
echo (close the app window to return here)
echo.
"%EXE%"
echo.
echo App closed with exit code %ERRORLEVEL%.
echo Full build log: %ROOT%build-log.txt
echo.
pause
exit /b 0

:failed
echo.
echo ============================ BUILD FAILED ============================
echo.
type "%ROOT%build-log.txt"
echo.
echo --------------------------------------------------------------------
echo Send the lines that contain "error" to the assistant.
echo Full log kept here: %ROOT%build-log.txt
echo.
pause
exit /b 1
