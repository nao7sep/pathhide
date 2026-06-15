#!/usr/bin/env bash
set -euo pipefail

# run-dev: run the app from source with live reload, in its loosest configuration.
# For active coding and debugging. The strict, production-faithful launchers are
# run-built (launch the existing production build without rebuilding) and rebuild
# (build from clean in release config, then launch).
#
# On macOS, features gated by TCC (access to protected folders) need the
# ad-hoc-signed bundle from rebuild/run-built — `dotnet run` has no bundle
# identity, so those prompts won't fire here.

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT_FILE="$REPO_DIR/PathHide.csproj"

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
    echo "pathhide run-dev failed with exit code $status."
    read -r -p "Press Enter to close..."
  fi
}

trap 'pause_on_failure $?' EXIT

require_command dotnet

cd "$REPO_DIR"

log_step "Restoring packages required for launch"
dotnet restore "$PROJECT_FILE"

log_step "Starting PathHide (Debug, from source)"
dotnet run --project "$PROJECT_FILE"
