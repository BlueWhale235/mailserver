#!/usr/bin/env sh
set -eu

ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
CONFIGURATION="${CONFIGURATION:-Release}"
dotnet build "$ROOT/MailServer.slnx" -c "$CONFIGURATION"
if [ "${TEST:-false}" = "true" ]; then
  dotnet test "$ROOT/MailServer.slnx" -c "$CONFIGURATION" --no-build
fi
echo "Build output: $ROOT/builds"
