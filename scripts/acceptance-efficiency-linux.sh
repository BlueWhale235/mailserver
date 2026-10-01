#!/usr/bin/env bash
set -euo pipefail
# Requires a published self-contained CLI, Linux systemd and root cgroup access.
BINARY="$(realpath "${1:?Pass the self-contained Linux mailserver binary}")"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUN_ID="$(date -u +%Y%m%dT%H%M%SZ)-$$"
RUN_DIR="/var/tmp/mailserver-efficiency-$RUN_ID"
UNIT="mailserver-efficiency-$RUN_ID"
mkdir -p "$RUN_DIR/data"
cp "$BINARY" "$RUN_DIR/mailserver"
if [ -f "$(dirname "$BINARY")/libe_sqlite3.so" ]; then
    cp "$(dirname "$BINARY")/libe_sqlite3.so" "$RUN_DIR/"
fi
chmod 700 "$RUN_DIR/mailserver"
PASSWORD="$(python3 -c 'import secrets; print(secrets.token_urlsafe(24))')"
export MAILSERVER_CONFIG="$RUN_DIR/mailserver.json"
cd "$RUN_DIR"
./mailserver init --non-interactive --config "$MAILSERVER_CONFIG" --provider Sqlite \
    --connection "Data Source=$RUN_DIR/data/mailserver.db" --bind 127.0.0.1 \
    --http-port 25080 --https-port 0 --admin-password "$PASSWORD" >init-private.log 2>&1
chmod 600 init-private.log
python3 - "$MAILSERVER_CONFIG" <<'PY'
import json,sys
path=sys.argv[1]
data=json.load(open(path))
data['Storage']['RootPath']='data/messages'
data['Smtp']['Endpoints']=[{'Address':'127.0.0.1','Port':25280,'Security':'Plain','RequireAuthentication':False}]
data['Imap']['Endpoints']=[{'Address':'127.0.0.1','Port':25143,'Security':'Plain'}]
data['MessageSearch']={'Enabled':True,'BackfillBatchSize':16,'BackfillDelaySeconds':1}
data['SmtpConnectionReuse']={'Enabled':True}
data['EfficiencyDiagnostics']={'Enabled':True}
json.dump(data,open(path,'w'),indent=2)
PY
./mailserver domain add efficiency.invalid >seed.log 2>&1
./mailserver account create inbox@efficiency.invalid --password "$PASSWORD" >>seed.log 2>&1
python3 - "$RUN_DIR/data/mailserver.db" <<'PY'
import sqlite3,sys
db=sqlite3.connect(sys.argv[1])
db.execute('UPDATE mail_accounts SET QuotaBytes=?', (8*1024**3,))
db.commit()
PY
systemd-run --unit "$UNIT" --property=MemoryMax=1G --property=MemorySwapMax=0 \
    --property="WorkingDirectory=$RUN_DIR" --property="StandardOutput=append:$RUN_DIR/server.log" \
    --property="StandardError=append:$RUN_DIR/server.log" \
    --setenv="MAILSERVER_CONFIG=$MAILSERVER_CONFIG" "$RUN_DIR/mailserver" run all
trap 'systemctl stop "$UNIT" >/dev/null 2>&1 || true' EXIT
for _ in $(seq 1 60); do
    if python3 -c 'from urllib.request import urlopen; urlopen("http://127.0.0.1:25080/api/v1/health/ready",timeout=2)' >/dev/null 2>&1; then break; fi
    sleep 1
done
CGROUP="$(systemctl show "$UNIT" --property=ControlGroup --value)"
printf '%s\n' "$RUN_DIR" >"$ROOT/builds/acceptance/efficiency-current-run.txt"
(
    while systemctl is-active --quiet "$UNIT"; do
        python3 - "/sys/fs/cgroup$CGROUP" <<'PY'
import json,sys,time
from pathlib import Path
p=Path(sys.argv[1])
print(json.dumps({'time':time.time(), 'memoryBytes':int((p/'memory.current').read_text()),
    'swapBytes':int((p/'memory.swap.current').read_text()), 'events':(p/'memory.events').read_text()}))
PY
        sleep 5
    done
) >memory.jsonl &
SAMPLER=$!
set +e
python3 "$ROOT/scripts/efficiency-sustained-traffic.py" --duration "${DURATION_SECONDS:-1800}" \
    --output "$RUN_DIR/traffic.jsonl" >traffic-summary.json 2>traffic.err
RESULT=$?
set -e
kill "$SAMPLER" >/dev/null 2>&1 || true
PID="$(systemctl show "$UNIT" --property=MainPID --value)"
if [ "$PID" != 0 ]; then cat "/proc/$PID/status" >process-status.txt; fi
systemctl show "$UNIT" --property=MemoryPeak --property=MemoryCurrent --property=Result >state.txt
cat "/sys/fs/cgroup$CGROUP/memory.events" >memory-events.txt
printf 'Results: %s\n' "$RUN_DIR"
exit "$RESULT"
