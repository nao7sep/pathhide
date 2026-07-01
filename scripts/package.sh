#!/usr/bin/env bash
set -euo pipefail

# Package PathHide for macOS into dist/: a .dmg installer + a portable .zip of
# the .app. Run by CI on macos-latest and runnable locally. Per the
# app-release-conventions, the packaging complexity lives here so the release
# workflow just calls this one script.

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO"

APP_NAME="PathHide"
PROJECT="src/PathHide/PathHide.csproj"
VERSION="$(grep -m1 '<Version>' Directory.Build.props | sed -E 's/.*<Version>(.*)<\/Version>.*/\1/')"

rm -rf publish dist
mkdir -p dist

# Self-contained arm64 publish. The AssembleMacAppBundle target (Directory.Build.targets)
# assembles and ad-hoc-signs publish/PathHide.app as part of the publish.
dotnet publish "$PROJECT" -c Release -r osx-arm64 --self-contained true -o publish

APP="publish/$APP_NAME.app"
[ -d "$APP" ] || { echo "expected $APP was not produced by publish" >&2; exit 1; }

# Portable: a zip of the .app (ditto preserves symlinks + the ad-hoc signature).
ditto -c -k --keepParent "$APP" "dist/$APP_NAME-$VERSION-mac.zip"

# Installer: a compressed .dmg holding the .app plus an /Applications alias so the
# user can drag-install. hdiutil is built into macOS — no extra tool to install.
STAGE="$(mktemp -d)"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "$APP_NAME" -srcfolder "$STAGE" -ov -format UDZO "dist/$APP_NAME-$VERSION.dmg" >/dev/null
rm -rf "$STAGE"

echo "macOS artifacts in dist/:"
ls -la dist/
