#!/usr/bin/env bash
set -euo pipefail

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
    echo "pathhide update-packages failed with exit code $status."
    read -r -p "Press Enter to close..."
  fi
}

stop_pathhide() {
  pkill -x "PathHide" 2>/dev/null || true
  pkill -f "dotnet run --project $PROJECT_FILE" 2>/dev/null || true
  pkill -f "PathHide.dll" 2>/dev/null || true
}

trap 'pause_on_failure $?' EXIT

require_command dotnet

cd "$REPO_DIR"

log_step "Stopping running PathHide instances"
stop_pathhide

log_step "Restoring current packages"
dotnet restore "$PROJECT_FILE"

log_step "Updating NuGet package references"
dotnet package update --project "$PROJECT_FILE"

log_step "Applying vulnerable package updates"
dotnet package update --project "$PROJECT_FILE" --vulnerable

log_step "Cleaning previous build outputs"
rm -rf "$REPO_DIR/bin" "$REPO_DIR/obj"

log_step "Building PathHide"
dotnet build "$PROJECT_FILE"
