#!/usr/bin/env python3
"""Run the bounded remote endurance check and retain API and memory evidence."""
import argparse, datetime, json, ssl, subprocess, sys, time
from pathlib import Path
from urllib.parse import urlencode
from urllib.request import Request, urlopen

parser=argparse.ArgumentParser()
for name in ('host','ssh-key','credentials','ca-cert','output'):
    parser.add_argument('--'+name,required=True)
args=parser.parse_args()
output=Path(args.output)
credentials=json.loads(Path(args.credentials).read_text(encoding='utf-8-sig'))
tls=ssl.create_default_context(cafile=args.ca_cert)
token=None
def api(path,body=None,method=None):
    request=Request(f'https://{args.host}:5000/api/v1'+path,
        data=json.dumps(body).encode() if body is not None else None,
        headers={'Content-Type':'application/json',**({'Authorization':'Bearer '+token} if token else {})},method=method)
    with urlopen(request,context=tls,timeout=60) as response:return json.load(response)
def ssh(command):
    return subprocess.check_output(['ssh','-n','-i',args.ssh_key,'-o','BatchMode=yes','root@'+args.host,command],
        stdin=subprocess.DEVNULL,text=True,encoding='utf-8',timeout=60)
def save(name,data):
    (output/name).write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
token=api('/auth/token',{'userName':credentials['user'],'password':credentials['password']})['accessToken']
api('/settings/resources',{'profileId':'1gb'},'PUT')
ssh('supervisorctl restart mailserver:*')
time.sleep(5)
api('/health/ready')
api('/operations/performance/diagnostic',{'enabled':True},'POST')
started=datetime.datetime.now(datetime.timezone.utc)
pid=int(ssh('supervisorctl pid mailserver:mailserver_00').strip())
remote='/tmp/mailserver-efficiency-results/after-endurance-memory.jsonl'
monitor=subprocess.Popen(['ssh','-n','-i',args.ssh_key,'-o','BatchMode=yes','root@'+args.host,
    f'python3 /tmp/mailserver-efficiency-monitor.py --pid {pid} --duration 2100 --output {remote}'],
    stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
save('after-endurance-start.json',{'startedAt':started.isoformat(),'pid':pid,'durationSeconds':1800})
with (output/'after-endurance-summary.json').open('w') as summary, (output/'after-endurance-error.log').open('w') as errors:
    load=subprocess.Popen([sys.executable,str(Path(__file__).with_name('efficiency-sustained-traffic.py')),
        '--host',args.host,'--smtp-port','25','--health-url',f'https://{args.host}:5000/api/v1/health/ready',
        '--ca-cert',args.ca_cert,'--recipient',credentials['mailAccount'],'--duration','1800',
        '--output',str(output/'after-endurance.jsonl')],stdout=summary,stderr=errors)
    stopped=None
    while load.poll() is None:
        time.sleep(20)
        samples=[json.loads(line) for line in ssh('tail -n 30 '+remote).splitlines() if line.strip()]
        if any(not row.get('alive') or row.get('VmSwapBytes',0)>128*1024**2
               or row.get('MemAvailableBytes',1<<60)<150*1024**2 for row in samples):
            stopped='memory threshold'
            load.terminate()
            load.wait(timeout=10)
            break
ended=datetime.datetime.now(datetime.timezone.utc)
save('after-endurance-result.json',{'startedAt':started.isoformat(),'endedAt':ended.isoformat(),
    'exitCode':load.returncode,'stopped':stopped})
bounds={'from':started.isoformat(),'to':ended.isoformat()}
token=api('/auth/token',{'userName':credentials['user'],'password':credentials['password']})['accessToken']
for kind in ('operation','stage','sql'):
    save('after-endurance-diagnostic-'+kind+'.json',api('/operations/performance/summary?'+urlencode({**bounds,'kind':kind})))
(output/'after-endurance-memory.jsonl').write_text(ssh('cat '+remote),encoding='utf-8')
api('/operations/performance/diagnostic',{'enabled':False},'POST')
print(json.dumps({'exitCode':load.returncode,'stopped':stopped}),flush=True)
