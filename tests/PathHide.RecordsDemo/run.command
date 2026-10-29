#!/usr/bin/env bash
set -euo pipefail

# Opens PathHide's real Records window over a throwaway folder of 10,000 records with new ones arriving
# (see Program.cs). Avalonia.Native starts only in a bundle opened through Launch Services, so this
# publishes the demo as its own bundle with the app's bundle assembly and opens it as the launchers open
# PathHide, recording it as this repository's runtime; the next launcher, or this one, asks it to quit.
# A PathHide the launchers started is left running: the demo uses its own folder and no single-instance lease.

DEMO_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$DEMO_DIR/../.." && pwd)"
OUT_DIR="$DEMO_DIR/bin/bundle"
BUNDLE="$OUT_DIR/PathHide.RecordsDemo.app"
source "$REPO_DIR/scripts/launcher-runtime.sh"

running="$(recorded_runtime_pid "$REPO_DIR")"
if [[ -n "$running" && "$(ps -p "$running" -o comm=)" == */PathHide.RecordsDemo ]]; then
  stop_recorded_runtime "Records demo" "$REPO_DIR"
fi
rm -rf "$OUT_DIR"
dotnet publish "$DEMO_DIR/PathHide.RecordsDemo.csproj" -c Release -r osx-arm64 --self-contained true -o "$OUT_DIR/"
launch_bundle "Records demo" "$REPO_DIR" "$BUNDLE"
