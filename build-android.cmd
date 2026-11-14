@echo off
rem ============================================================================
rem  TmLab: build for Android, deploy to a device/emulator and LAUNCH the app.
rem
rem  Usage: double-click this file, or run it from the command line:
rem      C:\...\memTransaction\maui\build-android.cmd
rem
rem  Before running, start an Android emulator (Visual Studio: Tools ->
rem  Android Emulator Manager) or connect a phone with USB debugging on.
rem  The script checks connected devices with adb and stops with a hint
rem  if there is none.
rem
rem  How it works: the project builds only the Windows target by default
rem  (WindowsOnly=true in TmLab.csproj). Setting WindowsOnly=false here adds
rem  the Android target; "-f net10.0-android" builds only Android.
rem
rem  (This file is ASCII-only on purpose: cmd.exe mangles Cyrillic text.)
rem ============================================================================

setlocal
set "ROOT=%~dp0"
cd /d "%ROOT%TmLab" || (echo [ERROR] Project folder not found: %ROOT%TmLab & pause & exit /b 1)

set "WindowsOnly=false"

echo ==== 1/3  looking for adb ====
set "ADB="
where adb >nul 2>&1 && set "ADB=adb"
if not defined ADB if exist "%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe" set "ADB=%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe"
if not defined ADB if defined ANDROID_HOME if exist "%ANDROID_HOME%\platform-tools\adb.exe" set "ADB=%ANDROID_HOME%\platform-tools\adb.exe"
if not defined ADB if defined ANDROID_SDK_ROOT if exist "%ANDROID_SDK_ROOT%\platform-tools\adb.exe" set "ADB=%ANDROID_SDK_ROOT%\platform-tools\adb.exe"

if not defined ADB (
  echo [ERROR] Android SDK was not found (adb is missing, error XA5300).
  echo Install the Android SDK: Visual Studio Installer -^> Modify -^>
  echo make sure the ".NET Multi-platform App UI development" workload
  echo is installed - it includes the Android SDK. Then run this script
  echo again. Or install the Android command-line tools manually and
  echo set the ANDROID_HOME environment variable to the SDK folder.
  echo.
  pause
  exit /b 1
)
echo adb: %ADB%

echo.
echo ==== 2/3  connected devices ====
"%ADB%" devices
echo.
"%ADB%" devices | findstr /r /c:"device$" >nul
if errorlevel 1 (
  echo [ERROR] No Android device or emulator is running.
  echo.
  echo Start one of these and run this script again:
  echo   - emulator: Visual Studio -^> Tools -^> Android Emulator Manager -^> Start,
  echo     or Android Studio -^> AVD Manager;
  echo   - phone: enable USB debugging, connect by cable, accept the prompt
  echo     on the phone screen.
  echo.
  pause
  exit /b 1
)

echo.
echo ==== 3/3  build, deploy and launch ====
echo (first run restores packages - needs access to nuget.org)
call dotnet run -f net10.0-android
if errorlevel 1 (
  echo.
  echo [ERROR] dotnet run failed. If the error mentions a missing SDK or
  echo workload, check "dotnet workload list" - the "android" workload and
  echo the Android SDK must be installed.
  echo.
  pause
  exit /b 1
)
echo.
echo Done.
pause
exit /b 0
