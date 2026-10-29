#!/usr/bin/env bash
set -euo pipefail
mpa_api=${1:?API level required}
mpa_target=default
if [[ "$mpa_api" == "37.0" ]]; then mpa_target=google_apis; fi
mpa_image_dir="$ANDROID_HOME/system-images/android-$mpa_api/$mpa_target/x86_64"
if [[ ! -s "$mpa_image_dir/system.img" || ! -s "$mpa_image_dir/ramdisk.img" || ! -s "$mpa_image_dir/package.xml" ]]; then
  printf 'Required pinned system image is missing or incomplete: %s\n' "$mpa_image_dir" >&2
  exit 1
fi
mpa_avd_dir="$HOME/.android/avd/mpa.avd"
mkdir -p "$mpa_avd_dir" build/diagnostics
cat > "$HOME/.android/avd/mpa.ini" <<EOF
avd.ini.encoding=UTF-8
path=$mpa_avd_dir
target=android-$mpa_api
EOF
cat > "$mpa_avd_dir/config.ini" <<EOF
AvdId=mpa
avd.ini.displayname=MPA API $mpa_api
abi.type=x86_64
hw.cpu.arch=x86_64
hw.cpu.ncore=2
hw.ramSize=3072
hw.lcd.width=1080
hw.lcd.height=1920
hw.lcd.density=420
hw.keyboard=yes
hw.gpu.enabled=yes
hw.gpu.mode=swiftshader
disk.dataPartition.size=2G
image.sysdir.1=system-images/android-$mpa_api/$mpa_target/x86_64/
tag.id=$mpa_target
tag.display=$mpa_target
target=android-$mpa_api
showDeviceFrame=no
EOF
# A fresh CI host has no previous update check or snapshot. Configure that
# explicitly, along with metrics opt-out, instead of relying on GUI prompts.
printf '# Fresh CI host\n' > "$HOME/.android/emu-update-last-check.ini"
# Compile both APKs before boot: cold Google APIs images can starve their SIM
# broadcast receiver when Kotlin/DEX compilation competes with system startup.
# Keep the same strict gate; connectedDebugAndroidTest still executes every test.
python3 scripts/gradle_checks.py --log build/diagnostics/device-build.log -- assembleDebug assembleDebugAndroidTest
timeout --kill-after=2s 20s adb start-server
emulator -avd mpa -port 5554 -no-window -gpu swiftshader -noaudio -no-boot-anim \
  -camera-back none -camera-front none -no-metrics -no-snapshot \
  -feature -QuickbootFileBacked,-Vulkan -show-kernel > build/diagnostics/emulator.log 2>&1 &
mpa_emulator_pid=$!
mpa_logcat_pid=""
mpa_logcat_started=false
mpa_cleanup() {
  if [[ -n "$mpa_logcat_pid" ]]; then
    kill "$mpa_logcat_pid" 2>/dev/null || true
    wait "$mpa_logcat_pid" 2>/dev/null || true
  elif [[ "$mpa_logcat_started" != true ]]; then
    # Before a device appears, plain logcat waits forever. Diagnostics must not
    # turn a failed emulator launch into a job timeout or mask the original error.
    timeout --kill-after=2s 10s adb logcat -d -v threadtime > build/diagnostics/logcat.txt 2>&1 || true
  fi
  timeout --kill-after=2s 10s adb emu kill || true
  wait "$mpa_emulator_pid" || true
  cat build/diagnostics/emulator.log
}
trap mpa_cleanup EXIT
mpa_booted=false
mpa_boot_deadline=$((SECONDS+600))
while (( SECONDS < mpa_boot_deadline )); do
  if ! kill -0 "$mpa_emulator_pid" 2>/dev/null; then exit 1; fi
  if timeout --kill-after=2s 5s adb devices | grep -q $'emulator-5554\tdevice$'; then
    if [[ "$(timeout --kill-after=2s 5s adb shell getprop sys.boot_completed | tr -d '\r')" == "1" ]]; then
      mpa_booted=true
      break
    fi
  fi
  sleep 5
done
if [[ "$mpa_booted" != true ]]; then
  adb devices -l
  printf 'Emulator did not complete boot\n' >&2
  exit 1
fi
# Preparation can intentionally restart adbd/framework for the narrowly verified
# API29 boot-time heap mismatch. Preserve its diagnostics separately; a streaming
# connection opened before adb root can terminate before any app test starts.
# Start the owned, uninterrupted TEST stream only after preparation actually joins.
adb logcat -d -v threadtime > build/diagnostics/logcat-before-preparation.txt
python3 scripts/test_emulator_heap.py
if [[ "$mpa_api" == "29" ]]; then
  python3 scripts/emulator_heap.py "$mpa_api"
fi
adb logcat -d -v threadtime > build/diagnostics/logcat-after-preparation.txt
# Keep every test event/allocation trace. An unexpected disconnect during tests
# remains blocking; never reconnect, truncate, retry tests or replace this stream.
adb logcat -v threadtime > build/diagnostics/logcat.txt 2> build/diagnostics/logcat-reader.txt &
mpa_logcat_pid=$!
mpa_logcat_started=true
adb shell settings put global window_animation_scale 0
adb shell settings put global transition_animation_scale 0
adb shell settings put global animator_duration_scale 0
# Do not send MENU to the still-starting launcher: its input ANR dialog can
# steal focus from otherwise healthy app tests. Unlock through WindowManager.
adb shell settings put system screen_off_timeout 1800000
adb shell settings put global stay_on_while_plugged_in 7
adb shell input keyevent KEYCODE_WAKEUP
adb shell wm dismiss-keyguard
bash scripts/run_device_checks.sh
if ! kill -0 "$mpa_logcat_pid" 2>/dev/null; then
  printf 'Runtime log capture stopped before native checks finished\n' >&2
  exit 1
fi
kill "$mpa_logcat_pid"
wait "$mpa_logcat_pid" 2>/dev/null || true
mpa_logcat_pid=""
python3 scripts/test_runtime_capture.py
python3 scripts/runtime_capture.py
