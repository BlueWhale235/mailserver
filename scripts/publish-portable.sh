#!/usr/bin/env sh
set -eu

ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
OUTPUT="${OUTPUT:-$ROOT/builds/publish}"
if [ "$#" -gt 0 ]; then
  RUNTIMES="$*"
else
  ARCH="$(uname -m)"
  case "$ARCH" in
    x86_64|amd64) RUNTIMES="linux-x64" ;;
    aarch64|arm64) RUNTIMES="linux-arm64" ;;
    *) echo "Unsupported architecture: $ARCH" >&2; exit 1 ;;
  esac
fi
for RID in $RUNTIMES; do
  TEMP_ARTIFACTS="$(mktemp -d "${TMPDIR:-/tmp}/mailserver-publish-$RID.XXXXXX")"
  trap 'rm -rf "$TEMP_ARTIFACTS"' EXIT INT TERM
  dotnet publish "$ROOT/src/MailServer.Cli/MailServer.Cli.csproj" \
      -c Release \
      -r "$RID" \
      --self-contained true \
      -p:ArtifactsPath="$TEMP_ARTIFACTS" \
      -p:PublishSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true \
      -p:DebugType=None \
      -o "$OUTPUT/$RID"
  rm -rf "$TEMP_ARTIFACTS"
  trap - EXIT INT TERM
done

if [ "${REMOVE_BUILD_CACHE:-false}" = "true" ]; then
  rm -rf "$ROOT/builds/bin" "$ROOT/builds/obj"
fi
