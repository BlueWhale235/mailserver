#!/usr/bin/env python3
"""Measure client mailbox queries and full-message IMAP downloads."""
import argparse,concurrent.futures,datetime,imaplib,json,math,re,ssl,subprocess,time
from pathlib import Path
from urllib.parse import urlencode
from urllib.request import Request,urlopen
p=argparse.ArgumentParser()
for key in ('host','ssh-key','credentials','ca-cert','output'):p.add_argument('--'+key,required=True)
a=p.parse_args()
out=Path(a.output)
c=json.loads(Path(a.credentials).read_text(encoding='utf-8-sig'))
tls=ssl.create_default_context(cafile=a.ca_cert)
token=None
def api(path,body=None,method=None):
    r=Request(f'https://{a.host}:5000/api/v1'+path,data=json.dumps(body).encode() if body is not None else None,
        headers={'Content-Type':'application/json',**({'Authorization':'Bearer '+token} if token else {})},method=method)
    with urlopen(r,context=tls,timeout=60) as response:return json.load(response)
def ssh(command):
    return subprocess.check_output(['ssh','-n','-i',a.ssh_key,'-o','BatchMode=yes','root@'+a.host,command],
        stdin=subprocess.DEVNULL,text=True,timeout=90)
def client():
    conn=imaplib.IMAP4_SSL(a.host,993,ssl_context=tls,timeout=90)
    assert conn.login(c['mailAccount'],c['mailPassword'])[0]=='OK'
    assert conn.select('INBOX',readonly=True)[0]=='OK'
    return conn
def download(uid,expected):
    start=time.perf_counter()
    try:
        with client() as conn:
            status,data=conn.uid('FETCH',str(uid),'(BODY.PEEK[])')
            size=sum(len(row[1]) for row in data if isinstance(row,tuple))
            if status!='OK' or size!=expected:raise RuntimeError('literal size mismatch')
        return {'seconds':time.perf_counter()-start,'bytes':size,'ok':True}
    except Exception as error:return {'seconds':time.perf_counter()-start,'ok':False,'error':type(error).__name__}
def emit(row):
    with (out/'imap-pressure.jsonl').open('a') as f:f.write(json.dumps(row)+'\n')
    print(json.dumps(row),flush=True)
token=api('/auth/token',{'userName':c['user'],'password':c['password']})['accessToken']
for profile in ('1gb','32gb'):
    api('/settings/resources',{'profileId':profile},'PUT')
    ssh('supervisorctl restart mailserver:*')
    time.sleep(5)
    api('/operations/performance/diagnostic',{'enabled':True},'POST')
    start=datetime.datetime.now(datetime.timezone.utc)
    pid=int(ssh('supervisorctl pid mailserver:mailserver_00').strip())
    remote='/tmp/mailserver-efficiency-results/imap-'+profile+'-memory.jsonl'
    subprocess.Popen(['ssh','-n','-i',a.ssh_key,'root@'+a.host,
        f'python3 /tmp/mailserver-efficiency-monitor.py --pid {pid} --duration 900 --output {remote}'],
        stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    with client() as conn:
        status,rows=conn.uid('FETCH','1:*','(UID RFC822.SIZE)')
        assert status=='OK'
        sizes={int(m[0]):int(m[1]) for row in rows if isinstance(row,bytes)
            for m in re.findall(rb'UID (\d+).*?RFC822.SIZE (\d+)',row)}
        selected={size:next((uid,length) for uid,length in sizes.items()
            if size*1024**2-1024<length<size*1024**2+2048) for size in (1,10)}
        begin=time.perf_counter()
        status,result=conn.status('INBOX','(MESSAGES UNSEEN UIDNEXT)')
        assert status=='OK'
        emit({'profile':profile,'operation':'STATUS','seconds':time.perf_counter()-begin,'response':[r.decode() for r in result]})
        begin=time.perf_counter()
        status,result=conn.uid('SEARCH',None,'SUBJECT','"efficiency"')
        assert status=='OK'
        emit({'profile':profile,'operation':'SEARCH SUBJECT','seconds':time.perf_counter()-begin,
            'matched':len(result[0].split()),'mailboxMessages':len(sizes)})
    stopped=False
    for size in (1,10):
        uid,length=selected[size]
        emit({'profile':profile,'operation':'warmup','sizeMiB':size,**download(uid,length)})
        for concurrency in ((2,) if profile=='1gb' else (2,3)):
            for round_number in range(1,6):
                begin=time.time()
                with concurrent.futures.ThreadPoolExecutor(max_workers=concurrency) as executor:
                    results=list(executor.map(lambda _:download(uid,length),range(concurrency*2)))
                wall=time.time()-begin
                samples=[json.loads(line) for line in ssh('tail -n 60 '+remote).splitlines() if line.strip()]
                samples=[r for r in samples if r['time']>=begin]
                swap=max((r.get('VmSwapBytes',0) for r in samples),default=0)
                available=min((r.get('MemAvailableBytes',1<<60) for r in samples),default=1<<60)
                latencies=sorted(r['seconds'] for r in results if r['ok'])
                healthy=api('/health/ready').get('status')=='ready'
                emit({'profile':profile,'sizeMiB':size,'concurrency':concurrency,'round':round_number,
                    'accepted':len(latencies),'attempted':len(results),'MiBPerSecond':sum(r.get('bytes',0) for r in results)/1024**2/wall,
                    'p95Seconds':latencies[math.ceil(len(latencies)*.95)-1] if latencies else None,
                    'peakRssBytes':max((r.get('VmRSSBytes',0) for r in samples),default=0),
                    'peakSwapBytes':swap,'healthy':healthy,'errors':[r for r in results if not r['ok']]})
                if not healthy or len(latencies)!=len(results) or swap>128*1024**2 or available<150*1024**2:
                    stopped=True;break
                time.sleep(3)
            if stopped:break
        if stopped:break
    time.sleep(5)
    bounds={'from':start.isoformat(),'to':datetime.datetime.now(datetime.timezone.utc).isoformat()}
    for kind in ('operation','stage','sql'):
        result=api('/operations/performance/summary?'+urlencode({**bounds,'kind':kind}))
        (out/f'imap-{profile}-diagnostic-{kind}.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    (out/f'imap-{profile}-memory.jsonl').write_text(ssh('cat '+remote),encoding='utf-8')
api('/operations/performance/diagnostic',{'enabled':False},'POST')
