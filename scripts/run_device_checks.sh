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
mpa_test_status=0
mkdir -p build/diagnostics
python3 scripts/test_native_stages.py
python3 scripts/native_stages.py 2>&1 | tee build/diagnostics/device.log || mpa_test_status=$?

# Keep all images, including intermediate navigation states, when assertions fail.
mpa_screenshots=app/build/reports/androidTests/screenshots
mkdir -p "$mpa_screenshots"
if adb pull /sdcard/Download/MPA "$mpa_screenshots/raw"; then
  : > "$mpa_screenshots/capture-map.txt"
  for pair in offline-cleanup-preview:mpa-offline-cleanup-preview offline-cleanup-confirm:mpa-offline-cleanup-confirm device-folders-foreign:mpa-device-folders-foreign device-folder-gallery:mpa-device-folder-gallery search-empty-stable:mpa-search-empty-stable blocks-balanced-rtl-large:mpa-blocks-balanced-rtl-large device-camera-video:mpa-device-camera-video people-unified-merged:mpa-people-unified-merged device-camera-details:mpa-device-camera-details person-rename-dark-rtl-large:mpa-person-rename-dark-rtl-large person-merge-light:mpa-person-merge-light menu-photo-view:mpa-menu-photo-view menu-gallery-return:mpa-menu-gallery-return arrival-mosaic-stable:mpa-arrival-mosaic-stable arrival-mosaic-newest:mpa-arrival-mosaic-newest arrival-rows-stable:mpa-arrival-rows-stable arrival-rows-newest:mpa-arrival-rows-newest arrival-square-stable:mpa-arrival-square-stable arrival-square-newest:mpa-arrival-square-newest import-item-recovery:mpa-import-item-recovery import-retry-complete:mpa-import-retry-complete import-xmp-independent:mpa-import-xmp-independent device-copy-confirmation:mpa-device-copy-confirmation saved-metadata-repair:mpa-saved-metadata-repair details-landscape:mpa-details-landscape sources-refined-dark-rtl:mpa-sources-refined-dark-rtl sources-refined-light-rtl:mpa-sources-refined-light-rtl details-audit-dark-rtl:mpa-details-audit-dark-rtl details-audit-light-rtl:mpa-details-audit-light-rtl ux-scroll-measured:mpa-ux-scroll-measured base-preview-recovered:mpa-base-preview-recovered date-scrub-gallery:mpa-date-scrub-gallery date-scrub-rtl-large:mpa-date-scrub-rtl-large date-scrub-jump:mpa-date-scrub-jump date-scrub-unknown:mpa-date-scrub-unknown drag-selection-mosaic:mpa-drag-selection-mosaic drag-selection-rows:mpa-drag-selection-rows drag-selection-square:mpa-drag-selection-square drag-selection-rtl-large:mpa-drag-selection-rtl-large blocks-gallery:mpa-blocks-gallery blocks-square:mpa-blocks-square thumbnail-quality-base:mpa-thumbnail-quality-base thumbnail-quality-sharp:mpa-thumbnail-quality-sharp album-management:mpa-album-management details-overview:mpa-details-overview collections-compact:mpa-collections-compact people-visual:mpa-people-visual gallery-bar-hidden:mpa-gallery-bar-hidden gallery-bar-returned:mpa-gallery-bar-returned motion-dark-rtl:mpa-motion-dark-rtl trash-browser:mpa-trash-browser trash-restore:mpa-trash-restore trash-local-protected:mpa-trash-local-protected import-recovery:mpa-import-recovery continuous-mosaic:mpa-continuous-mosaic continuous-square:mpa-continuous-square viewer-immersive:mpa-viewer-immersive viewer-immersive-light:mpa-viewer-immersive-light viewer-immersive-dark-rtl:mpa-viewer-immersive-dark-rtl takeout-preview-light:mpa-takeout-preview-light takeout-preview-dark-rtl:mpa-takeout-preview-dark-rtl viewer-actions:mpa-viewer-actions management-menu:mpa-management-menu mosaic-gallery:mpa-mosaic-gallery mosaic-square:mpa-mosaic-square floating-date-light-large:mpa-floating-date-light-large floating-date-dark-rtl-large:mpa-floating-date-dark-rtl-large density-large:mpa-density-large date-navigation:mpa-date-navigation polish-light-large:mpa-polish-light-large polish-dark-rtl-large:mpa-polish-dark-rtl-large management-sources:mpa-management-sources management-activity:mpa-management-activity management-import:mpa-management-import takeout-imported-people:mpa-imported-people management-light-large:mpa-management-light-large management-dark-rtl-large:mpa-management-dark-rtl-large device-preview:mpa-edit import-preview:mpa-import shell-preview:mpa-shell shell-navigation-light:mpa-navigation-light shell-navigation-dark:mpa-navigation-dark shell-navigation-large:mpa-navigation-large shell-navigation-rtl:mpa-navigation-rtl search-discovery:mpa-search-discovery search-results:mpa-search-results search-filters:mpa-search-filters search-light-large:mpa-search-light-large search-dark-rtl-large:mpa-search-dark-rtl-large multi-selection:mpa-selection collections-hub:mpa-collections viewer-details:mpa-viewer-details viewer-remote-details:mpa-viewer-remote-details viewer-details-light-large:mpa-details-light-large viewer-details-dark-rtl-large:mpa-details-dark-rtl-large; do
    mpa_source=${pair%%:*}
    mpa_target=${pair#*:}
    printf '%s.png %s.png\n' "$mpa_source" "$mpa_target" >> "$mpa_screenshots/capture-map.txt"
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
if [[ "$mpa_test_status" == 0 && ! -f "$mpa_screenshots/raw/search-benchmark.json" ]]; then
  printf 'Missing required native search benchmark evidence\n' >&2
  exit 1
fi
if [[ "$mpa_test_status" == 0 && ! -f "$mpa_screenshots/raw/takeout-people-benchmark.json" ]]; then
  printf 'Missing required native imported-people benchmark evidence\n' >&2
  exit 1
fi
if [[ "$mpa_test_status" == 0 && ! -f "$mpa_screenshots/raw/trash-benchmark.json" ]]; then
  printf 'Missing required native Trash benchmark evidence\n' >&2
  exit 1
fi
if [[ "$mpa_test_status" == 0 && ! -f "$mpa_screenshots/raw/date-scrub-benchmark.json" ]]; then
  printf 'Missing required native date-scrubbing benchmark evidence\n' >&2
  exit 1
fi
if [[ "$mpa_test_status" == 0 && ! -f "$mpa_screenshots/raw/ux-scroll-benchmark.json" ]]; then
  printf 'Missing required native UX scroll benchmark evidence\n' >&2
  exit 1
fi
if [[ "$mpa_test_status" == 0 && ! -f "$mpa_screenshots/raw/canonical-people-benchmark.json" ]]; then
  printf 'Missing required canonical people and indexed-device benchmark evidence\n' >&2
  exit 1
fi
if [[ "$mpa_test_status" == 0 ]]; then
  python3 scripts/test_png_evidence.py
  python3 scripts/test_collect_png_evidence.py
  python3 scripts/collect_png_evidence.py "$mpa_screenshots"
fi
exit "$mpa_test_status"
