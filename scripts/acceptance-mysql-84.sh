#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
command -v docker >/dev/null || { echo 'Docker is required.' >&2; exit 2; }
command -v dotnet >/dev/null || { echo '.NET SDK is required.' >&2; exit 2; }
RUN_ID="$(date -u +%Y%m%dT%H%M%SZ)-$$"
CONTAINER="mailserver-acceptance-${RUN_ID,,}"
RESULT_DIR="${RESULT_DIR:-$ROOT/builds/acceptance/mysql-$RUN_ID}"
mkdir -p "$RESULT_DIR"
PASSWORD="$(tr -dc 'A-Za-z0-9' </dev/urandom | head -c 32 || true)"
test "${#PASSWORD}" -eq 32
cleanup() { docker stop "$CONTAINER" >/dev/null 2>&1 || true; }
trap cleanup EXIT

docker run -d --rm --name "$CONTAINER" \
  -e MYSQL_ROOT_PASSWORD="$PASSWORD" \
  -e MYSQL_DATABASE=mailserver_acceptance \
  -p 127.0.0.1::3306 mysql:8.4 >"$RESULT_DIR/container-id.txt"
READY=0
for _ in $(seq 1 90); do
  if docker exec "$CONTAINER" mysqladmin ping -h localhost -uroot -p"$PASSWORD" --silent >/dev/null 2>&1; then
    READY=1
    break
  fi
  sleep 2
done
if [ "$READY" -ne 1 ]; then
  docker logs "$CONTAINER" >"$RESULT_DIR/mysql.log" 2>&1
  echo 'MySQL did not become ready. See mysql.log.' >&2
  exit 1
fi
PORT="$(docker port "$CONTAINER" 3306/tcp | sed -n 's/.*://p' | head -n 1)"
test -n "$PORT"
export MAILSERVER_TEST_MYSQL_CONNECTION="Server=127.0.0.1;Port=$PORT;Database=mailserver_acceptance;User=root;Password=$PASSWORD"
{
  printf 'baseline=2180361\ncommit=%s\n' "$(git -C "$ROOT" rev-parse HEAD)"
  printf 'mysql_image=mysql:8.4\nport=%s\nstarted_utc=%s\n' "$PORT" "$(date -u +%FT%TZ)"
  printf 'test=MySqlMigrationVersionContentionAndTwoWorkerLeases\n'
} >"$RESULT_DIR/parameters.txt"
set +e
dotnet test "$ROOT/tests/MailServer.Tests/MailServer.Tests.csproj" -c Release \
  --filter 'FullyQualifiedName~MySqlMigrationVersionContentionAndTwoWorkerLeases' \
  --results-directory "$RESULT_DIR" --logger 'trx;LogFileName=results.trx' \
  >"$RESULT_DIR/test.log" 2>&1
RESULT=$?
set -e
printf 'exit_code=%s\nfinished_utc=%s\n' "$RESULT" "$(date -u +%FT%TZ)" >>"$RESULT_DIR/parameters.txt"
docker logs "$CONTAINER" >"$RESULT_DIR/mysql.log" 2>&1
echo "Results: $RESULT_DIR"
exit "$RESULT"
