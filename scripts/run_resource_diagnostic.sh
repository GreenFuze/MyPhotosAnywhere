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
# Only this disposable emulator permits hidden test-tracing hooks. Production
# app sources never use them; the device setting is restored on every exit.
mpa_previous_hidden_policy=$(adb shell settings get global hidden_api_policy | tr -d '\r')
adb shell settings put global hidden_api_policy 1
adb logcat -c
adb logcat -v threadtime > build/diagnostics/allocation-live-logcat.txt &
mpa_diagnostic_log_pid=$!
mpa_restore_diagnostic() {
  kill "$mpa_diagnostic_log_pid" 2>/dev/null || true
  if [[ "$mpa_previous_hidden_policy" == null ]]; then
    adb shell settings delete global hidden_api_policy
  else
    adb shell settings put global hidden_api_policy "$mpa_previous_hidden_policy"
  fi
}
trap mpa_restore_diagnostic EXIT
adb install -r -t app/build/outputs/apk/debug/app-debug.apk
adb install -r -t app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk
adb shell am instrument -w -r -e package com.myphotosanywhere.mpa -e notAnnotation com.myphotosanywhere.mpa.device.DeviceLibraryStage com.myphotosanywhere.mpa.test/com.myphotosanywhere.mpa.ResourceDiagnosticRunner | tee build/diagnostics/focused-camera-instrumentation.txt
adb exec-out run-as com.myphotosanywhere.mpa cat cache/resource-allocation-traces.json > build/diagnostics/resource-allocation-traces.json
mkdir -p app/build/reports/androidTests/screenshots
adb pull /sdcard/Download/MPA app/build/reports/androidTests/screenshots/raw
python3 - <<'VERIFY_FOCUSED'
from pathlib import Path
import re,json,subprocess
text=Path('build/diagnostics/focused-camera-instrumentation.txt').read_text(encoding='utf-8')
assert re.search(r'OK \(152 tests\)',text),text
assert len(re.findall(r'^INSTRUMENTATION_STATUS_CODE: 0\s*$',text,re.M))==152,text
assert 'FAILURES!!!' not in text and 'INSTRUMENTATION_FAILED' not in text
assert 'allocationTracingRecordsTheDeliberateTestOnlySentinel' in text
allocations=json.loads(Path('build/diagnostics/resource-allocation-traces.json').read_text(encoding='utf-8'))
assert allocations['api'] in (35,37) and allocations['resultCode']==-1,allocations
assert any('ResourceDiagnosticTest.abandonDiagnosticSentinel' in trace['stack'] for trace in allocations['traces']),allocations
tree=subprocess.check_output(['git','rev-parse','HEAD:app/src/main'],text=True).strip()
assert tree=='446cd292c59d12585ea50aadcb2e2100ab2c3af5',tree
Path('build/diagnostics/focused-source-proof.json').write_text(json.dumps({'applicationSource':'cb04850c4534716c278a1d062151b5ca8d961788','appMainTree':tree,'allocationTests':152,'releaseAcceptance':False},indent=2)+'\n')
VERIFY_FOCUSED
