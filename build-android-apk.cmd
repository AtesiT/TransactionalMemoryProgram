@echo off
rem ============================================================================
rem  TmLab: build an Android APK file (no device needed).
rem  The APK is created in TmLab\bin\Debug\net10.0-android\com.example.tmlab-Signed.apk -
rem  copy it to a phone (USB, messenger, cloud) and install it there.
rem  On the phone allow installs from unknown sources for the app you use
rem  to open the file (browser, file manager, messenger).
rem
rem  Usage: double-click this file, or run it from the command line:
rem      C:\...\memTransaction\maui\build-android-apk.cmd
rem
rem  This builds a DEBUG apk signed with the debug key - it installs on
rem  any phone. A release build would need a signing keystore.
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

echo ==== check Android SDK ====
set "SDK="
if defined ANDROID_HOME if exist "%ANDROID_HOME%\platform-tools\adb.exe" set "SDK=%ANDROID_HOME%"
if not defined SDK if defined ANDROID_SDK_ROOT if exist "%ANDROID_SDK_ROOT%\platform-tools\adb.exe" set "SDK=%ANDROID_SDK_ROOT%"
if not defined SDK if exist "%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe" set "SDK=%LOCALAPPDATA%\Android\Sdk"
if defined SDK goto sdk_found
echo [ERROR] Android SDK was not found (error XA5300).
echo The "android" workload is installed, but the Android SDK itself
echo - platform-tools, platforms, build-tools - is missing.
echo.
echo Install it: Visual Studio Installer -^> Modify -^> make sure the
echo ".NET Multi-platform App UI development" workload is installed -
echo it includes the Android SDK. Then run this script again.
echo.
echo Or install the Android command-line tools manually from
echo https://developer.android.com/studio#command-line-tools-only
echo and set the ANDROID_HOME environment variable to the SDK folder.
echo.
pause
exit /b 1
:sdk_found
echo Android SDK: %SDK%

echo.
echo ==== build APK ====
echo (first run restores packages - needs access to nuget.org)
call dotnet build -f net10.0-android -c Debug
if errorlevel 1 goto failed

set "APK=bin\Debug\net10.0-android\com.example.tmlab-Signed.apk"
if not exist "%APK%" (
  echo [ERROR] APK not found: %APK%
  echo APK files in bin:
  dir /s /b "bin\*.apk"
  echo.
  pause
  exit /b 1
)

echo.
echo ============================ APK READY ============================
echo.
echo File: %ROOT%TmLab\%APK%
echo.
echo Copy this file to the phone and install it.
echo On the phone, allow installs from unknown sources for the app
echo you use to open the file (browser, file manager, messenger).
echo.
echo Opening the folder with the APK...
start "" "%ROOT%TmLab\bin\Debug\net10.0-android"
echo.
pause
exit /b 0

:failed
echo.
echo ============================ BUILD FAILED ============================
echo.
echo If the error mentions a missing SDK or workload, check
echo "dotnet workload list" - the "android" workload and the
echo Android SDK must be installed.
echo.
pause
exit /b 1
