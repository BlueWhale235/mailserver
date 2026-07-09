#!/usr/bin/env sh
set -eu

ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
CONFIGURATION="${CONFIGURATION:-Release}"
OUTPUT="${OUTPUT:-$ROOT/builds/plugins/$CONFIGURATION}"

for PLUGIN in MailServer.MimePlugin MailServer.GreylistingPlugin; do
  TARGET="$OUTPUT/$PLUGIN"
  dotnet publish "$ROOT/plugins/$PLUGIN/$PLUGIN.csproj" \
    -c "$CONFIGURATION" \
    -o "$TARGET"
  test -f "$TARGET/plugin.json"
  test -f "$TARGET/$PLUGIN.dll"
  grep -Eq '"system"[[:space:]]*:[[:space:]]*true' "$TARGET/plugin.json"
done

printf 'Plugin packages: %s\n' "$OUTPUT"
