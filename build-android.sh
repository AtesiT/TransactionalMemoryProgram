#!/usr/bin/env bash
# ============================================================================
#  TmLab: build for Android, deploy to a device/emulator and LAUNCH the app.
#
#  Usage: ./build-android.sh
#
#  Before running, start an Android emulator (Android Studio -> AVD Manager)
#  or connect a phone with USB debugging on. The script checks connected
#  devices with adb and stops with a hint if there is none.
#
#  The Android SDK is looked up in ANDROID_HOME, ANDROID_SDK_ROOT and
#  ~/Android/Sdk. To keep the SDK on another disk/partition, point
#  ANDROID_HOME at it (e.g. ANDROID_HOME=/mnt/data/Android/Sdk).
#  Virtual devices (AVD) live in ~/.android/avd by default; override with
#  ANDROID_AVD_HOME to keep them on another disk too.
# ============================================================================

set -u
ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT/TmLab" || { echo "[ERROR] Project folder not found: $ROOT/TmLab"; exit 1; }

echo "==== looking for adb ===="
ADB=""
command -v adb >/dev/null 2>&1 && ADB="adb"
[ -z "$ADB" ] && [ -n "${ANDROID_HOME:-}" ] && [ -x "$ANDROID_HOME/platform-tools/adb" ] && ADB="$ANDROID_HOME/platform-tools/adb"
[ -z "$ADB" ] && [ -n "${ANDROID_SDK_ROOT:-}" ] && [ -x "$ANDROID_SDK_ROOT/platform-tools/adb" ] && ADB="$ANDROID_SDK_ROOT/platform-tools/adb"
[ -z "$ADB" ] && [ -x "$HOME/Android/Sdk/platform-tools/adb" ] && ADB="$HOME/Android/Sdk/platform-tools/adb"

if [ -z "$ADB" ]; then
  echo "[ERROR] Android SDK was not found (adb is missing, error XA5300)."
  echo "Install it via Android Studio (Settings -> SDK Location) or with the"
  echo "command-line tools, then set ANDROID_HOME to the SDK folder."
  exit 1
fi
echo "adb: $ADB"

echo
echo "==== connected devices ===="
"$ADB" devices
echo
if ! "$ADB" devices | awk '$2 == "device" { f = 1 } END { exit !f }'; then
  echo "[ERROR] No Android device or emulator is running."
  echo
  echo "Start one of these and run this script again:"
  echo "  - emulator: Android Studio -> AVD Manager -> Start;"
  echo "  - phone: enable USB debugging, connect by cable, accept the prompt"
  echo "    on the phone screen."
  exit 1
fi

echo
echo "==== build, deploy and launch ===="
echo "(first run restores packages - needs access to nuget.org)"
dotnet run -f net10.0-android || {
  echo
  echo "[ERROR] dotnet run failed. If the error mentions a missing SDK or"
  echo "workload, check \"dotnet workload list\" - the \"android\" workload and"
  echo "the Android SDK must be installed."
  exit 1
}
echo
echo "Done."
exit 0
