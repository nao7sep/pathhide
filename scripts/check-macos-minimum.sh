#!/usr/bin/env bash
# Usage: check-macos-minimum.sh <path to .app>
# Fails when the bundle's declared minimum macOS (Info.plist LSMinimumSystemVersion) is older than
# the newest minimum any of its Mach-O files declares, so macOS refuses the app with its own message
# on a system it cannot run on instead of letting it start and crash. The bundle assembly in
# Directory.Build.targets runs it before signing.
set -euo pipefail

bundle="$1"
declared="$(/usr/libexec/PlistBuddy -c 'Print :LSMinimumSystemVersion' "$bundle/Contents/Info.plist")"
required="$(find "$bundle" -type f -print0 \
  | xargs -0 file \
  | grep 'Mach-O' \
  | cut -d: -f1 \
  | while IFS= read -r binary; do vtool -show-build "$binary" 2>/dev/null | awk '/minos/ {print $2}'; done \
  | sort -V | tail -n 1)"

if [[ -z "$required" ]]; then
  echo "No Mach-O minimum was found in $bundle." >&2
  exit 1
fi
if [[ "$(printf '%s\n%s\n' "$declared" "$required" | sort -V | head -n 1)" != "$required" ]]; then
  echo "Info.plist declares macOS $declared, but the bundle's binaries need macOS $required." >&2
  exit 1
fi
