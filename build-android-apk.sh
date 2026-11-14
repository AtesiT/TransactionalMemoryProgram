#!/usr/bin/env bash
# ============================================================================
#  TmLab: build an Android APK file (no device needed).
#  The APK is created in TmLab/bin/Debug/net10.0-android/com.example.tmlab-Signed.apk -
#  copy it to a phone (USB, messenger, cloud) and install it there.
#  On the phone allow installs from unknown sources for the app you use
#  to open the file (browser, file manager, messenger).
#
#  Usage: ./build-android-apk.sh
#
#  This builds a DEBUG apk signed with the debug key - it installs on
#  any phone. A release build would need a signing keystore.
#
#  The Android SDK is looked up in ANDROID_HOME, ANDROID_SDK_ROOT and
#  ~/Android/Sdk. To keep the SDK on another disk/partition, point
#  ANDROID_HOME at it (e.g. ANDROID_HOME=/mnt/data/Android/Sdk).
# ============================================================================

set -u
ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT/TmLab" || { echo "[ERROR] Project folder not found: $ROOT/TmLab"; exit 1; }

echo "==== check Android SDK ===="
SDK=""
[ -n "${ANDROID_HOME:-}" ] && [ -x "$ANDROID_HOME/platform-tools/adb" ] && SDK="$ANDROID_HOME"
[ -z "$SDK" ] && [ -n "${ANDROID_SDK_ROOT:-}" ] && [ -x "$ANDROID_SDK_ROOT/platform-tools/adb" ] && SDK="$ANDROID_SDK_ROOT"
[ -z "$SDK" ] && [ -x "$HOME/Android/Sdk/platform-tools/adb" ] && SDK="$HOME/Android/Sdk"
if [ -z "$SDK" ]; then
  echo "[ERROR] Android SDK was not found (error XA5300)."
  echo "The \"android\" workload is installed, but the Android SDK itself"
  echo "- platform-tools, platforms, build-tools - is missing."
  echo
  echo "Install it via Android Studio (Settings -> SDK Location) or with the"
  echo "command-line tools, then set ANDROID_HOME to the SDK folder"
  echo "(e.g. ANDROID_HOME=/mnt/data/Android/Sdk for another disk)."
  exit 1
fi
echo "Android SDK: $SDK"

echo
echo "==== build APK ===="
echo "(first run restores packages - needs access to nuget.org)"
dotnet build -f net10.0-android -c Debug || exit 1

APK="bin/Debug/net10.0-android/com.example.tmlab-Signed.apk"
if [ ! -f "$APK" ]; then
  echo "[ERROR] APK not found: $APK"
  echo "APK files in bin:"
  find bin -name '*.apk'
  exit 1
fi

echo
echo "============================ APK READY ============================"
echo
echo "File: $ROOT/TmLab/$APK"
echo
echo "Copy this file to the phone and install it."
echo "On the phone, allow installs from unknown sources for the app"
echo "you use to open the file (browser, file manager, messenger)."
exit 0
