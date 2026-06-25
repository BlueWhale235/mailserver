#!/usr/bin/env sh
set -eu

ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
OUTPUT="${OUTPUT:-$ROOT/builds/publish}"

copy_deployment_assets() {
  TARGET="$1"

  cp "$ROOT/docker-compose.yml" "$TARGET/docker-compose.yml"
  cp "$ROOT/Dockerfile" "$TARGET/Dockerfile"
  cp "$ROOT/.env.example" "$TARGET/.env.example"
  cp "$ROOT/.env.example" "$TARGET/.env"
  cp "$ROOT/mailserver.example.json" "$TARGET/mailserver.example.json"
  cp "$ROOT/mailserver.example.json" "$TARGET/mailserver.json"
  cp "$ROOT/README.md" "$TARGET/README.md"

  mkdir -p "$TARGET/certificates" "$TARGET/plugins" "$TARGET/scripts"
  cp "$ROOT/certificates/README.md" "$TARGET/certificates/README.md"
  cp "$ROOT/plugins/.gitkeep" "$TARGET/plugins/.gitkeep"
  cp "$ROOT/scripts/"*.ps1 "$ROOT/scripts/"*.sh "$TARGET/scripts/"

  # Dockerfile builds the individual services from source, so include its
  # complete build context in every deployment package.
  cp "$ROOT/global.json" "$TARGET/global.json"
  cp "$ROOT/Directory.Build.props" "$TARGET/Directory.Build.props"
  cp "$ROOT/Directory.Packages.props" "$TARGET/Directory.Packages.props"
  cp "$ROOT/MailServer.slnx" "$TARGET/MailServer.slnx"
  mkdir -p "$TARGET/src"
  cp -R "$ROOT/src/." "$TARGET/src/"
}

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
  mkdir -p "$OUTPUT/$RID"
  TEMP_ARTIFACTS="$(mktemp -d "${TMPDIR:-/tmp}/mailserver-publish-$RID.XXXXXX")"
  trap 'rm -rf "$TEMP_ARTIFACTS"' EXIT INT TERM
  dotnet publish "$ROOT/src/MailServer.Cli/MailServer.Cli.csproj" \
      -c Release \
      -r "$RID" \
      --self-contained true \
      -p:ArtifactsPath="$TEMP_ARTIFACTS" \
      -p:PublishSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true \
      -p:ValidateExecutableReferencesMatchSelfContained=false \
      -p:DebugType=None \
      -p:DebugSymbols=false \
      -o "$OUTPUT/$RID"
  find "$OUTPUT/$RID" -maxdepth 1 -type f -name '*.pdb' -delete
  copy_deployment_assets "$OUTPUT/$RID"
  rm -rf "$TEMP_ARTIFACTS"
  trap - EXIT INT TERM
done

if [ "${REMOVE_BUILD_CACHE:-false}" = "true" ]; then
  rm -rf "$ROOT/builds/bin" "$ROOT/builds/obj"
fi
