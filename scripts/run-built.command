#!/usr/bin/env bash
set -euo pipefail

# run-built: launch the EXISTING ad-hoc-signed .app bundle without rebuilding, so
# it starts instantly. This is the daily-use launcher and the one that carries the
# bundle identity TCC needs (protected-folder prompts, etc.). It never publishes
# or signs — if you changed source, run rebuild first.

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
APP_BUNDLE="$REPO_DIR/publish/PathHide.app"
APP_EXECUTABLE="$APP_BUNDLE/Contents/MacOS/PathHide"
RUNTIME_TOKEN="run-built-$$-$(date +%s)-$RANDOM"
source "$SCRIPT_DIR/launcher-runtime.sh"

log_step() {
  printf '\n==> %s\n' "$1"
}

pause_on_failure() {
  local status="$1"
  if [[ "$status" -ne 0 && ( "$status" -lt 128 || "$status" -gt 143 ) ]]; then
    echo
    echo "pathhide run-built failed with exit code $status."
    read -r -p "Press Enter to close..."
  fi
}

trap 'pause_on_failure $?' EXIT

cd "$REPO_DIR"

# No publish, no codesign here: this launcher must start instantly. If there is
# no usable bundle yet, stop and point at rebuild rather than launching something
# stale or empty.
if [[ ! -d "$APP_BUNDLE/Contents/MacOS" ]]; then
  echo "No build found — run rebuild first."
  exit 1
fi

# Age tracks the actual build: rebuild resets Contents/MacOS, but never touches
# the .app dir's own mtime — stat'ing the bundle root would report a stale date.
built_at="$(stat -f '%Sm' -t '%Y-%m-%d %H:%M:%S %Z' "$APP_BUNDLE/Contents/MacOS" 2>/dev/null || echo 'unknown')"
claim_launcher_runtime "$RUNTIME_TOKEN" "$REPO_DIR"
stop_owned_runtime dotnet "PathHide" "$REPO_DIR" "$REPO_DIR/src/PathHide/PathHide.csproj" "PathHide" "$APP_EXECUTABLE"
log_step "Launching the existing build (built: $built_at)"
echo "If you changed source since then, run rebuild instead."

# `open` routes through Launch Services, which registers the bundle identity with
# TCC and triggers permission prompts on first access.
open -n "$APP_BUNDLE"
wait_for_owned_runtime dotnet "PathHide" "$REPO_DIR" "$REPO_DIR/src/PathHide/PathHide.csproj" "PathHide" "$APP_EXECUTABLE" 30
