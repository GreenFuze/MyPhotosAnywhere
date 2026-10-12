#!/usr/bin/env bash
set -euo pipefail

# Synthetic fixtures stay independent of live accounts and network timing.
adb shell svc wifi disable
adb shell svc data disable
mpa_test_status=0
mkdir -p build/diagnostics
python3 scripts/gradle_checks.py --log build/diagnostics/device.log -- connectedDebugAndroidTest || mpa_test_status=$?

# Keep all images, including intermediate navigation states, when assertions fail.
mpa_screenshots=app/build/reports/androidTests/screenshots
mkdir -p "$mpa_screenshots"
if adb pull /sdcard/Download/MPA "$mpa_screenshots/raw"; then
  for pair in device-preview:mpa-edit import-preview:mpa-import shell-preview:mpa-shell shell-navigation-light:mpa-navigation-light shell-navigation-dark:mpa-navigation-dark shell-navigation-large:mpa-navigation-large shell-navigation-rtl:mpa-navigation-rtl multi-selection:mpa-selection collections-hub:mpa-collections viewer-details:mpa-viewer-details viewer-remote-details:mpa-viewer-remote-details viewer-details-light-large:mpa-details-light-large viewer-details-dark-rtl-large:mpa-details-dark-rtl-large; do
    mpa_source=${pair%%:*}
    mpa_target=${pair#*:}
    if [[ -f "$mpa_screenshots/raw/$mpa_source.png" ]]; then
      cp "$mpa_screenshots/raw/$mpa_source.png" "$mpa_screenshots/$mpa_target.png"
    elif [[ "$mpa_test_status" == 0 ]]; then
      printf 'Missing required screenshot: %s\n' "$mpa_source" >&2
      exit 1
    fi
  done
elif [[ "$mpa_test_status" == 0 ]]; then
  exit 1
fi
if [[ "$mpa_test_status" == 0 && ! -f "$mpa_screenshots/raw/timeline-benchmark.json" ]]; then
  printf 'Missing required native timeline benchmark evidence\n' >&2
  exit 1
fi
exit "$mpa_test_status"
