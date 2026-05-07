#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT_FILE="$REPO_DIR/PathHide.csproj"
APP_BUNDLE="$REPO_DIR/publish/PathHide.app"
INFO_PLIST="$REPO_DIR/macOS/Info.plist"

# Map the host CPU to a .NET runtime identifier so the same script works on
# Apple Silicon and Intel Macs without a manual flag.
ARCH="$(uname -m)"
case "$ARCH" in
  arm64)  RID="osx-arm64" ;;
  x86_64) RID="osx-x64"   ;;
  *)
    echo "Unsupported macOS architecture: $ARCH (expected arm64 or x86_64)." >&2
    exit 1
    ;;
esac

PUBLISH_DIR="$REPO_DIR/bin/Release/net10.0/$RID/publish"

log_step() {
  printf '\n==> %s\n' "$1"
}

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Missing required command: $1" >&2
    exit 1
  fi
}

pause_on_failure() {
  local status="$1"
  if [[ "$status" -ne 0 && "$status" -ne 130 ]]; then
    echo
    echo "pathhide run failed with exit code $status."
    read -r -p "Press Enter to close..."
  fi
}

trap 'pause_on_failure $?' EXIT

require_command dotnet
require_command codesign

cd "$REPO_DIR"

log_step "Publishing self-contained $RID build (host arch $ARCH)"
dotnet publish "$PROJECT_FILE" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -o "$PUBLISH_DIR"

log_step "Assembling app bundle"
# Reset MacOS so stale assemblies from a previous build can't linger.
rm -rf "${APP_BUNDLE:?}/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources"

# Copy the publish output (binary + native dylibs + managed DLLs) into MacOS/.
cp -R "$PUBLISH_DIR/." "$APP_BUNDLE/Contents/MacOS/"

# Drop in the Info.plist so TCC has a bundle identity and usage strings.
cp "$INFO_PLIST" "$APP_BUNDLE/Contents/Info.plist"

log_step "Ad-hoc signing bundle"
# `--sign -` is the ad-hoc identity. --force overwrites prior signatures (each
# rebuild produces a new cdhash). --deep recursively re-signs nested bundles
# (Avalonia's native dylibs ship pre-signed with Avalonia's identity; we replace
# those with our ad-hoc signature so the whole bundle has one consistent identity).
codesign --force --deep --sign - "$APP_BUNDLE"

# Verify the signature attached cleanly. `codesign --verify` exits non-zero if
# the bundle isn't recognized as signed code.
codesign --verify --verbose=1 "$APP_BUNDLE"

log_step "Launching"
# `open` routes through Launch Services, which is what registers the app's
# bundle identity with TCC and triggers permission prompts on first access.
open "$APP_BUNDLE"
