#!/usr/bin/env sh
set -eu

ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
OUTPUT="${OUTPUT:-$ROOT/builds/publish}"

copy_deployment_assets() {
  TARGET="$1"

  cp "$ROOT/.env.example" "$TARGET/.env.example"
  cp "$ROOT/.env.example" "$TARGET/.env"
  cp "$ROOT/mailserver.example.json" "$TARGET/mailserver.example.json"
  cp "$ROOT/mailserver.example.json" "$TARGET/mailserver.json"
  cp "$ROOT/README.md" "$TARGET/README.md"

  mkdir -p "$TARGET/certificates" "$TARGET/plugins" "$TARGET/security-assets" "$TARGET/wwwroot" "$TARGET/logs"
  cp "$ROOT/certificates/README.md" "$TARGET/certificates/README.md"
  cp "$ROOT/security-assets/README.md" "$TARGET/security-assets/README.md"
  cp -R "$ROOT/src/MailServer.Api/wwwroot/." "$TARGET/wwwroot/"

  ADMIN_SOURCE="$ROOT/src/MailServer.Admin/dist"
  if [ ! -f "$ADMIN_SOURCE/index.html" ]; then
    echo "The Vue administration bundle was not produced at '$ADMIN_SOURCE/index.html'." >&2
    exit 1
  fi
  # The API assembly embeds the SPA and extracts wwwroot/admin at startup.
}

remove_referenced_host_publish_files() {
  TARGET="$1"
  rm -f "$TARGET/appsettings.json"
  for PREFIX in MailServer.Api MailServer.Smtp MailServer.Imap MailServer.Worker; do
    rm -f "$TARGET/$PREFIX.exe" \
          "$TARGET/$PREFIX.deps.json" \
          "$TARGET/$PREFIX.runtimeconfig.json" \
          "$TARGET/$PREFIX.pdb" \
          "$TARGET/$PREFIX.dll"
    find "$TARGET" -maxdepth 1 -type f -name "$PREFIX.staticwebassets.*.json" -delete
  done
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
  # Republish into a clean directory. Copying over an existing tree leaves
  # stale bundles behind (for example a view that has since been deleted), and
  # the whole package is copied to production, so stale files ship silently.
  case "$RID" in
    "" | */* | . | ..)
      echo "Refusing to clean '$OUTPUT/$RID'." >&2
      exit 1
      ;;
  esac
  rm -rf "$OUTPUT/$RID"
  mkdir -p "$OUTPUT/$RID"
  TEMP_ARTIFACTS="$(mktemp -d "${TMPDIR:-/tmp}/mailserver-publish-$RID.XXXXXX")"
  PLATFORM_ARGS=""
  case "$RID" in
    *-x64) PLATFORM_ARGS="-p:Platform=x64" ;;
  esac
  trap 'rm -rf "$TEMP_ARTIFACTS"' EXIT INT TERM
  # shellcheck disable=SC2086
  dotnet publish "$ROOT/src/MailServer.Cli/MailServer.Cli.csproj" \
      -c Release \
      -r "$RID" \
      --self-contained true \
      $PLATFORM_ARGS \
      -p:ArtifactsPath="$TEMP_ARTIFACTS" \
      -p:PublishSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true \
      -p:ValidateExecutableReferencesMatchSelfContained=false \
      -p:DebugType=None \
      -p:DebugSymbols=false \
      -o "$OUTPUT/$RID"
  find "$OUTPUT/$RID" -maxdepth 1 -type f -name '*.pdb' -delete
  remove_referenced_host_publish_files "$OUTPUT/$RID"
  copy_deployment_assets "$OUTPUT/$RID"
  rm -rf "$TEMP_ARTIFACTS"
  trap - EXIT INT TERM
done

if [ "${REMOVE_BUILD_CACHE:-false}" = "true" ]; then
  rm -rf "$ROOT/builds/bin" "$ROOT/builds/obj"
fi
