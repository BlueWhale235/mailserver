#!/usr/bin/env python3
"""Compare resource profiles on one remote host and export its diagnostic API."""
import argparse,concurrent.futures,datetime,json,math,smtplib,ssl,statistics,subprocess,time,uuid
from pathlib import Path
from urllib.parse import urlencode
from urllib.request import Request,urlopen
from urllib.error import HTTPError

parser=argparse.ArgumentParser()
parser.add_argument('--host',required=True)
parser.add_argument('--ssh-key',type=Path,required=True)
parser.add_argument('--credentials',type=Path,required=True)
parser.add_argument('--ca-cert',type=Path,required=True)
parser.add_argument('--output',type=Path,required=True)
parser.add_argument('--tag',choices=['before','after'],required=True)
parser.add_argument('--profiles',default='1gb,32gb')
args=parser.parse_args()
args.output.mkdir(parents=True,exist_ok=True)
credentials=json.loads(args.credentials.read_text(encoding='utf-8-sig'))
tls=ssl.create_default_context(cafile=str(args.ca_cert))
base=f'https://{args.host}:5000/api/v1'
token=None

def api(path,body=None,method=None):
    request=Request(base+path,data=json.dumps(body).encode() if body is not None else None,
                    headers={'Content-Type':'application/json',**({'Authorization':'Bearer '+token} if token else {})},method=method)
    with urlopen(request,context=tls,timeout=60) as response: return json.load(response)

def ssh(command):
    return subprocess.check_output(['ssh','-n','-i',str(args.ssh_key),'-o','BatchMode=yes','-o','ConnectTimeout=15',
                                    'root@'+args.host,command],stdin=subprocess.DEVNULL,text=True,encoding='utf-8',timeout=120)

def save(name,data):
    (args.output/name).write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')

def emit(record):
    print(json.dumps(record,ensure_ascii=False),flush=True)
    with (args.output/(args.tag+'-pressure.jsonl')).open('a',encoding='utf-8') as output:
        output.write(json.dumps(record,ensure_ascii=False)+'\n')

def send(body):
    wire=(f'Message-ID: <{uuid.uuid4()}@remote-efficiency.invalid>\r\nFrom: sender@outside.invalid\r\n'
          f'To: {credentials["mailAccount"]}\r\nSubject: remote efficiency {args.tag}\r\n\r\n').encode()+body
    start=time.perf_counter()
    try:
        with smtplib.SMTP(args.host,25,timeout=90) as client:
            client.sendmail('sender@outside.invalid',credentials['mailAccount'],wire)
        return {'status':'ok','seconds':time.perf_counter()-start}
    except smtplib.SMTPResponseException as error:
        return {'status':'smtp'+str(error.smtp_code),'response':error.smtp_error.decode(errors='replace'),
                'seconds':time.perf_counter()-start}
    except Exception as error:
        return {'status':type(error).__name__,'seconds':time.perf_counter()-start}

token=api('/auth/token',{'userName':credentials['user'],'password':credentials['password']})['accessToken']
protection=api('/security/connection-protection')
save('original-protection.json',protection)
for name in ('smtpInbound','imap'):
    protection[name]={'perMinute':10000,'burst':2000,'maximumConcurrent':2000}
api('/security/connection-protection',protection,'PUT')

for profile in args.profiles.split(','):
    if profile not in ('1gb','32gb'): raise ValueError('unsupported profile')
    phase=args.tag+'-'+profile
    api('/settings/resources',{'profileId':profile},'PUT')
    ssh('supervisorctl restart mailserver:*')
    for attempt in range(30):
        try:
            api('/health/ready'); break
        except Exception: time.sleep(1)
    else: raise RuntimeError('service did not recover after profile restart')
    time.sleep(5)
    api('/operations/performance/diagnostic',{'enabled':True},'POST')
    save(phase+'-resource-status.json',api('/settings/resources/status'))
    started=datetime.datetime.now(datetime.timezone.utc)
    pid=int(ssh('supervisorctl pid mailserver:mailserver_00').strip())
    remote='/tmp/mailserver-efficiency-results/'+phase+'-memory.jsonl'
    subprocess.Popen(['ssh','-i',str(args.ssh_key),'-o','BatchMode=yes','root@'+args.host,
        f'python3 /tmp/mailserver-efficiency-monitor.py --pid {pid} --duration 900 --output {remote}'],
        stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    emit({'event':'phase-start','phase':phase,'pid':pid,'startedAt':started.isoformat()})
    stop_phase=False
    for size in (1,10):
        body=(b'x'*76+b'\r\n')*(size*1024*1024//78)
        warm=send(body)
        emit({'event':'warmup','phase':phase,'sizeMiB':size,**warm})
        if warm['status']!='ok': stop_phase=True; break
        for concurrency in ((2,) if profile=='1gb' else (2,3)):
            for round_number in range(1,6):
                begin=time.time()
                with concurrent.futures.ThreadPoolExecutor(max_workers=concurrency) as executor:
                    results=list(executor.map(send,[body]*(concurrency*2)))
                wall=time.time()-begin
                ordered=sorted(x['seconds'] for x in results if x['status']=='ok')
                raw=ssh('tail -n 60 '+remote)
                samples=[json.loads(line) for line in raw.splitlines() if line.strip()]
                samples=[sample for sample in samples if sample['time']>=begin]
                swap=max((sample.get('VmSwapBytes',0) for sample in samples),default=0)
                available=min((sample.get('MemAvailableBytes',1<<60) for sample in samples),default=1<<60)
                rss=max((sample.get('VmRSSBytes',0) for sample in samples),default=0)
                health=api('/health/ready').get('status')=='ready'
                record={'phase':phase,'sizeMiB':size,'concurrency':concurrency,'round':round_number,
                        'attempted':len(results),'accepted':len(ordered),'wallSeconds':wall,
                        'acceptedMiBPerSecond':len(ordered)*size/wall,
                        'p95Seconds':ordered[math.ceil(len(ordered)*.95)-1] if ordered else None,
                        'peakRssBytes':rss,'peakSwapBytes':swap,'minimumAvailableBytes':available,
                        'healthy':health,'errors':[x for x in results if x['status']!='ok']}
                emit(record)
                if not health or swap>128*1024**2 or available<150*1024**2 or record['errors']:
                    emit({'event':'phase-stop','phase':phase,'reason':'resource_or_delivery_threshold'})
                    stop_phase=True; break
                time.sleep(3)
            if stop_phase: break
        if stop_phase: break
    time.sleep(5)
    ended=datetime.datetime.now(datetime.timezone.utc)
    bounds={'from':started.isoformat(),'to':ended.isoformat()}
    for kind in ('operation','stage','sql'):
        try:
            diagnostic=api('/operations/performance/summary?'+urlencode({**bounds,'kind':kind}))
        except HTTPError as error:
            diagnostic={'unavailable':True,'httpStatus':error.code}
        save(phase+'-diagnostic-'+kind+'.json',diagnostic)
    operations=api('/operations/performance/operations?'+urlencode(bounds))
    save(phase+'-operations.json',operations)
    details=[]
    for row in sorted(operations['items'],key=lambda x:x['processingMilliseconds'],reverse=True)[:12]:
        try: details.append(api('/operations/performance/operations/'+row['id']))
        except HTTPError as error: details.append({'operationId':row['id'],'unavailable':True,'httpStatus':error.code})
    save(phase+'-details.json',details)
    (args.output/(phase+'-memory.jsonl')).write_text(ssh('cat '+remote),encoding='utf-8')
    emit({'event':'phase-end','phase':phase,'endedAt':ended.isoformat(),'sampledDetails':len(details)})

api('/operations/performance/diagnostic',{'enabled':False},'POST')
