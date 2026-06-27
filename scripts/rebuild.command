#!/usr/bin/env bash
set -euo pipefail

# rebuild: produce a fresh self-contained Release build and launch it. The macOS
# .app bundle is assembled and ad-hoc signed by the AssembleMacAppBundle target
# (Directory.Build.targets) as part of `dotnet publish`, so this launcher only
# publishes and opens the result — the same bundle the release workflow produces.
# Slow; run after changing source. run-built launches the existing bundle.

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT_FILE="$REPO_DIR/src/PathHide/PathHide.csproj"
APP_BUNDLE="$REPO_DIR/publish/PathHide.app"

# Map the host CPU to a .NET runtime identifier so a local rebuild runs natively
# on Apple Silicon and Intel Macs without a manual flag.
ARCH="$(uname -m)"
case "$ARCH" in
  arm64)  RID="osx-arm64" ;;
  x86_64) RID="osx-x64"   ;;
  *)
    echo "Unsupported macOS architecture: $ARCH (expected arm64 or x86_64)." >&2
    exit 1
    ;;
esac

pause_on_failure() {
  local status="$1"
  if [[ "$status" -ne 0 && "$status" -ne 130 ]]; then
    echo
    echo "pathhide rebuild failed with exit code $status."
    read -r -p "Press Enter to close..."
  fi
}

trap 'pause_on_failure $?' EXIT

cd "$REPO_DIR"

# Clear stale output, then publish. `dotnet publish` runs the bundling target,
# leaving publish/ holding only the signed .app.
rm -rf "$REPO_DIR/publish"
dotnet publish "$PROJECT_FILE" -c Release -r "$RID" --self-contained true -o "$REPO_DIR/publish"

open "$APP_BUNDLE"
