#!/usr/bin/env bash
set -euo pipefail

# Synthetic fixtures stay independent of live accounts and network timing.
mpa_native_serial=$(adb get-serialno | tr -d '\r')
if [[ "$mpa_native_serial" != emulator-* ]]; then
  printf 'Synthetic native tests require an emulator; no physical device settings were changed\n' >&2
  exit 1
fi
adb shell svc wifi disable
# sys.boot_completed can precede publication of ITelephony on a fresh API 29 AVD.
# Require its service before svc data, which otherwise dereferences a null binder.
mpa_phone_ready=false
for ((mpa_phone_attempt=0; mpa_phone_attempt<60; mpa_phone_attempt++)); do
  if adb shell service check phone | tr -d '\r' | grep -q '^Service phone: found$'; then
    mpa_phone_ready=true
    break
  fi
  sleep 1
done
if [[ "$mpa_phone_ready" != true ]]; then
  printf 'Emulator telephony service was not ready; offline tests cannot start\n' >&2
  exit 1
fi
adb shell svc data disable

# Diagnostic subset only; this does not replace any release suite or acceptance gate.
adb logcat -c
adb logcat -v threadtime > build/diagnostics/logcat.txt &
mpa_diagnostic_log_pid=$!
trap 'kill "$mpa_diagnostic_log_pid" 2>/dev/null || true' EXIT
adb install -r -t app/build/outputs/apk/debug/app-debug.apk
adb install -r -t app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk
adb shell am instrument -w -r -e class com.myphotosanywhere.mpa.timeline.MosaicDeviceTest,com.myphotosanywhere.mpa.timeline.GalleryArrivalDeviceTest,com.myphotosanywhere.mpa.ResourceDiagnosticTest com.myphotosanywhere.mpa.test/com.myphotosanywhere.mpa.ResourceDiagnosticRunner | tee build/diagnostics/focused-camera-instrumentation.txt
mkdir -p app/build/reports/androidTests/screenshots
adb pull /sdcard/Download/MPA app/build/reports/androidTests/screenshots/raw
python3 - <<'VERIFY_FOCUSED'
from pathlib import Path
import re,json,subprocess
text=Path('build/diagnostics/focused-camera-instrumentation.txt').read_text(encoding='utf-8')
assert re.search(r'OK \(7 tests\)',text),text
assert len(re.findall(r'^INSTRUMENTATION_STATUS_CODE: 0\s*$',text,re.M))==7,text
assert 'FAILURES!!!' not in text and 'INSTRUMENTATION_FAILED' not in text
assert 'allocationTracingRecordsTheDeliberateTestOnlySentinel' in text
tree=subprocess.check_output(['git','rev-parse','HEAD:app/src/main'],text=True).strip()
assert tree=='cfb8e76becaa683605966797d88629e00395f0d9',tree
Path('build/diagnostics/focused-source-proof.json').write_text(json.dumps({'applicationSource':'b65db27632aaac855be084fe9715a5393e90ca0e','appMainTree':tree,'allocationTests':7,'releaseAcceptance':False},indent=2)+'\n')
VERIFY_FOCUSED
