#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REQUESTED_VERSION="${1:-}"
if [[ ! "$REQUESTED_VERSION" =~ ^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$ ]]; then
  echo 'Usage: scripts/build-linux-release.sh vMAJOR.MINOR.PATCH[-prerelease]' >&2
  exit 2
fi
VERSION="${REQUESTED_VERSION#v}"
RELEASE_ROOT="$ROOT/builds/releases/v$VERSION"
TARGET="$RELEASE_ROOT/linux-x64"
if [[ -e "$RELEASE_ROOT" ]]; then
  echo "Release directory already exists: $RELEASE_ROOT" >&2
  exit 2
fi

mkdir -p "$ROOT/builds/releases"
STAGING="$(mktemp -d "$ROOT/builds/releases/.build-v$VERSION.XXXXXX")"
trap 'rm -rf -- "$STAGING"' EXIT

dotnet publish "$ROOT/src/MailServer.Cli/MailServer.Cli.csproj" \
  -c Release -r linux-x64 --self-contained true \
  -p:Platform=x64 \
  -p:ArtifactsPath="$STAGING/artifacts" \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:ValidateExecutableReferencesMatchSelfContained=false \
  -p:DebugType=None -p:DebugSymbols=false \
  -p:Version="$VERSION" \
  -p:InformationalVersion="$VERSION" \
  -p:IncludeSourceRevisionInInformationalVersion=false \
  -o "$STAGING/publish"

EXECUTABLE="$STAGING/publish/mailserver"
test -f "$EXECUTABLE"
test ! -e "$STAGING/publish/wwwroot/admin/index.html"
chmod +x "$EXECUTABLE"
ACTUAL_VERSION="$("$EXECUTABLE" --version)"
if [[ "$ACTUAL_VERSION" != "$VERSION" ]]; then
  echo "Published version mismatch: expected $VERSION, got $ACTUAL_VERSION" >&2
  exit 1
fi

mkdir -p "$STAGING/release/linux-x64"
cp "$EXECUTABLE" "$STAGING/release/linux-x64/mailserver"
SOURCE_COMMIT="$(git -C "$ROOT" rev-parse HEAD 2>/dev/null || printf unknown)"
SOURCE_STATE=clean
if ! git -C "$ROOT" diff --quiet || ! git -C "$ROOT" diff --cached --quiet; then
  SOURCE_STATE=modified
fi
printf 'Version: v%s\nRuntime: linux-x64\nCommit: %s\nSource state: %s\n' \
  "$VERSION" "$SOURCE_COMMIT" "$SOURCE_STATE" \
  > "$STAGING/release/linux-x64/VERSION.txt"
(cd "$STAGING/release/linux-x64" && sha256sum mailserver > SHA256SUMS)

mv "$STAGING/release" "$RELEASE_ROOT"
printf 'Built %s\n' "$TARGET/mailserver"
printf 'Version: %s\n' "$ACTUAL_VERSION"
(cd "$TARGET" && cat SHA256SUMS)
