#!/usr/bin/env bash
set -euo pipefail
mpa_api=${1:?API level required}
mpa_target=default
if [[ "$mpa_api" == "37.0" ]]; then mpa_target=google_apis; fi
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
hw.cpu.ncore=4
hw.ramSize=4096
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
adb start-server
emulator -avd mpa -port 5554 -no-window -gpu swiftshader -noaudio -no-boot-anim \
  -camera-back none -camera-front none -no-metrics -no-snapshot \
  -feature -QuickbootFileBacked,-Vulkan -show-kernel > build/diagnostics/emulator.log 2>&1 &
mpa_emulator_pid=$!
mpa_cleanup() {
  adb logcat -d > build/diagnostics/logcat.txt 2>&1 || true
  adb emu kill || true
  wait "$mpa_emulator_pid" || true
  cat build/diagnostics/emulator.log
}
trap mpa_cleanup EXIT
mpa_booted=false
for ((mpa_attempt=0; mpa_attempt<120; mpa_attempt++)); do
  if ! kill -0 "$mpa_emulator_pid" 2>/dev/null; then exit 1; fi
  if adb devices | grep -q $'emulator-5554\tdevice$'; then
    if [[ "$(adb shell getprop sys.boot_completed | tr -d '\r')" == "1" ]]; then
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
adb shell settings put global window_animation_scale 0
adb shell settings put global transition_animation_scale 0
adb shell settings put global animator_duration_scale 0
adb shell input keyevent 82
bash scripts/run_device_checks.sh
