#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
command -v docker >/dev/null || { echo 'Docker is required.' >&2; exit 2; }
command -v python3 >/dev/null || { echo 'Python 3 is required.' >&2; exit 2; }
RUN_ID="$(date -u +%Y%m%dT%H%M%SZ)-$$"
IMAGE="mailserver-acceptance:$RUN_ID"
CONTAINER="mailserver-512m-$RUN_ID"
RESULT_DIR="${RESULT_DIR:-$ROOT/builds/acceptance/linux-512m-$RUN_ID}"
DURATION_SECONDS="${DURATION_SECONDS:-300}"
ATTACHMENT_BYTES="${ATTACHMENT_BYTES:-4194304}"
mkdir -p "$RESULT_DIR/data"
RESULT_DIR="$(cd "$RESULT_DIR" && pwd)"
cleanup() { docker stop "$CONTAINER" >/dev/null 2>&1 || true; }
trap cleanup EXIT

docker build -f "$ROOT/Dockerfile" --build-arg PROJECT=MailServer.Cli -t "$IMAGE" "$ROOT" \
  >"$RESULT_DIR/build.log" 2>&1
PASSWORD="$(tr -dc 'A-Za-z0-9' </dev/urandom | head -c 32 || true)"
test "${#PASSWORD}" -eq 32
COMMON=(-v "$RESULT_DIR/data:/app/data" -e MAILSERVER_CONFIG=/app/data/mailserver.json
  -e Storage__RootPath=/app/data/messages -e SecurityAssets__RootPath=/app/data/security)
docker run --rm "${COMMON[@]}" "$IMAGE" init --non-interactive \
  --config /app/data/mailserver.json --provider Sqlite \
  --connection 'Data Source=/app/data/mailserver.db' --bind 0.0.0.0 \
  --http-port 8080 --https-port 0 --admin-password "$PASSWORD" \
  >"$RESULT_DIR/init-private.log" 2>&1
chmod 600 "$RESULT_DIR/init-private.log"
for domain in acceptance.test second.acceptance.test third.acceptance.test; do
  docker run --rm "${COMMON[@]}" "$IMAGE" domain add "$domain" >>"$RESULT_DIR/seed.log" 2>&1
  docker run --rm "${COMMON[@]}" "$IMAGE" account create "alice@$domain" \
    --password "$PASSWORD" >>"$RESULT_DIR/seed.log" 2>&1
done
docker run -d --name "$CONTAINER" --memory 512m --memory-swap 512m \
  -p 127.0.0.1::25 -p 127.0.0.1::143 -p 127.0.0.1::8080 \
  "${COMMON[@]}" "$IMAGE" run all >"$RESULT_DIR/container-id.txt"
SMTP_PORT="$(docker port "$CONTAINER" 25/tcp | sed -n 's/.*://p' | head -n 1)"
IMAP_PORT="$(docker port "$CONTAINER" 143/tcp | sed -n 's/.*://p' | head -n 1)"
test -n "$SMTP_PORT" && test -n "$IMAP_PORT"
{
  printf 'baseline=2180361\ncommit=%s\n' "$(git -C "$ROOT" rev-parse HEAD)"
  printf 'memory_limit=512MiB\nduration_seconds=%s\nattachment_bytes=%s\n' "$DURATION_SECONDS" "$ATTACHMENT_BYTES"
  printf 'smtp_port=%s\nimap_port=%s\nstarted_utc=%s\n' "$SMTP_PORT" "$IMAP_PORT" "$(date -u +%FT%TZ)"
} >"$RESULT_DIR/parameters.txt"
READY=0
for _ in $(seq 1 60); do
  if python3 -c 'import socket,sys; s=socket.create_connection(("127.0.0.1",int(sys.argv[1])),2); s.close()' "$SMTP_PORT" >/dev/null 2>&1; then
    READY=1
    break
  fi
  sleep 2
done
test "$READY" -eq 1 || { docker logs "$CONTAINER" >"$RESULT_DIR/server.log" 2>&1; exit 1; }

(
  while docker inspect "$CONTAINER" >/dev/null 2>&1; do
    printf '%s ' "$(date -u +%FT%TZ)"
    docker stats "$CONTAINER" --no-stream --format '{{.MemUsage}} {{.CPUPerc}}' 2>/dev/null || true
    sleep 5
  done
) >"$RESULT_DIR/memory-cpu.log" &
SAMPLER=$!
(
  for number in 1 2 3; do
    sleep 30
    docker run --rm "${COMMON[@]}" "$IMAGE" domain add "rotate$number.acceptance.test" || true
  done
) >"$RESULT_DIR/config-refresh.log" 2>&1 &
REFRESHER=$!
set +e
python3 "$ROOT/scripts/acceptance-traffic.py" --smtp-port "$SMTP_PORT" --imap-port "$IMAP_PORT" \
  --duration "$DURATION_SECONDS" --attachment-bytes "$ATTACHMENT_BYTES" \
  >"$RESULT_DIR/traffic.json" 2>"$RESULT_DIR/traffic.err"
TRAFFIC_RESULT=$?
set -e
docker inspect "$CONTAINER" --format '{{json .State}}' >"$RESULT_DIR/container-state.json"
CONTAINER_STATE="$(docker inspect "$CONTAINER" --format '{{.State.Running}} {{.State.OOMKilled}}')"
docker exec "$CONTAINER" cat /sys/fs/cgroup/memory.events >"$RESULT_DIR/memory-events.txt" 2>&1 || true
docker exec "$CONTAINER" cat /proc/1/status >"$RESULT_DIR/process-status.txt" 2>&1 || true
docker logs "$CONTAINER" >"$RESULT_DIR/server.log" 2>&1
grep 'Runtime resource sample' "$RESULT_DIR/server.log" >"$RESULT_DIR/runtime-metrics.log" || true
printf 'traffic_exit_code=%s\nfinished_utc=%s\n' "$TRAFFIC_RESULT" "$(date -u +%FT%TZ)" >>"$RESULT_DIR/parameters.txt"
printf 'container_running_and_oom_killed=%s\n' "$CONTAINER_STATE" >>"$RESULT_DIR/parameters.txt"
printf 'runtime_metric_samples=%s\n' "$(wc -l <"$RESULT_DIR/runtime-metrics.log")" >>"$RESULT_DIR/parameters.txt"
kill "$SAMPLER" "$REFRESHER" >/dev/null 2>&1 || true
wait "$SAMPLER" "$REFRESHER" 2>/dev/null || true
echo "Results: $RESULT_DIR"
if [ "$CONTAINER_STATE" != 'true false' ] || [ ! -s "$RESULT_DIR/runtime-metrics.log" ]; then
  echo 'The container stopped, was OOM-killed, or produced no runtime metrics.' >&2
  exit 1
fi
exit "$TRAFFIC_RESULT"
