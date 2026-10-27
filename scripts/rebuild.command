#!/usr/bin/env bash
set -euo pipefail

# rebuild: produce a fresh self-contained Release build and launch it. The macOS
# .app bundle is assembled and ad-hoc signed by the AssembleMacAppBundle target
# (Directory.Build.targets) as part of `dotnet publish`, so this launcher only
# publishes and opens the result — the same bundle the release workflow produces.
# Slow; run after changing source. run-built launches the existing bundle.

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
# The app name as Directory.Build.props states it, read as plain text so launching stays instant.
APP_NAME="$(sed -n 's:.*<Product>\(.*\)</Product>.*:\1:p' "$REPO_DIR/Directory.Build.props")"
PROJECT_FILE="$REPO_DIR/src/PathHide/PathHide.csproj"
APP_BUNDLE="$REPO_DIR/publish/$APP_NAME.app"
source "$SCRIPT_DIR/launcher-runtime.sh"

# Apple Silicon only, matching what the fleet ships and what package.sh
# publishes. Building an Intel binary locally would produce an artifact the
# project does not release and nobody tests, so an Intel host is refused
# outright rather than quietly handed a different product.
ARCH="$(uname -m)"
if [[ "$ARCH" != "arm64" ]]; then
  echo "$APP_NAME builds for Apple Silicon only; this host is $ARCH." >&2
  exit 1
fi
# The macOS runtime identifier the project declares.
RID="$(dotnet msbuild "$PROJECT_FILE" -getProperty:RuntimeIdentifiers | tr ';' '\n' | grep -m1 '^osx-')"

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
  if [[ "$status" -ne 0 && ( "$status" -lt 128 || "$status" -gt 143 ) ]]; then
    echo
    echo "$APP_NAME rebuild failed with exit code $status."
    read -r -p "Press Enter to close..."
  fi
}

trap 'pause_on_failure $?' EXIT

cd "$REPO_DIR"

require_command dotnet

# Clear stale output, then publish. `dotnet publish` runs the bundling target,
# leaving publish/ holding only the signed .app.
log_step "Clearing the previous build"
rm -rf "$REPO_DIR/publish"

log_step "Publishing $APP_NAME ($RID, Release, self-contained)"
dotnet publish "$PROJECT_FILE" -c Release -r "$RID" --self-contained true -o "$REPO_DIR/publish"

log_step "Launching the rebuilt app"
stop_recorded_runtime "$APP_NAME" "$REPO_DIR"
launch_bundle "$APP_NAME" "$REPO_DIR" "$APP_BUNDLE"
