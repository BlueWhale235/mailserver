#!/usr/bin/env python3
"""Fixed mailbox fixtures and persistent IMAP workloads for runtime tracing."""
import atexit,argparse,concurrent.futures,datetime,email.header,imaplib,json,os,smtplib,ssl,subprocess,time,uuid
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request,urlopen
parser=argparse.ArgumentParser()
for key in ('host','credentials','ca-cert','output'):parser.add_argument('--'+key,required=True)
parser.add_argument('--ssh-key',default='')
parser.add_argument('--local',action='store_true',help='Run collectors on this machine without SSH')
parser.add_argument('--tag',required=True)
parser.add_argument('--seed',action='store_true')
parser.add_argument('--trace',action='store_true')
parser.add_argument('--rounds',type=int,default=1)
parser.add_argument('--duration-seconds',type=int,default=0)
parser.add_argument('--maximum-swap-mib',type=int,default=128)
parser.add_argument('--pid-command',default='supervisorctl pid mailserver:mailserver_00')
parser.add_argument('--parallelism',type=int,default=2)
parser.add_argument('--messages-per-size',type=int,default=4)
parser.add_argument('--warmup',action='store_true')
parser.add_argument('--period-seconds',type=float,default=0)
parser.add_argument('--minimum-available-mib',type=int,default=150)
parser.add_argument('--allow-smtp-rejections',action='store_true')
parser.add_argument('--fetch-metadata',action='store_true')
parser.add_argument('--additional-pid-command',default='')
args=parser.parse_args()
out=Path(args.output);out.mkdir(parents=True,exist_ok=True)
credentials=json.loads(Path(args.credentials).read_text(encoding='utf-8-sig'))
tls=ssl.create_default_context(cafile=args.ca_cert)
token=None
def api(path,body=None,method=None):
    request=Request(f'https://{args.host}:5000/api/v1'+path,data=json.dumps(body).encode() if body is not None else None,
        headers={'Content-Type':'application/json',**({'Authorization':'Bearer '+token} if token else {})},method=method)
    with urlopen(request,context=tls,timeout=60) as response:
        data=response.read();return json.loads(data) if data else None
def remote_command(command):
    return ['bash','-lc',command] if args.local else ['ssh','-n','-i',args.ssh_key,'-o','BatchMode=yes','root@'+args.host,command]

def ssh(command):
    return subprocess.check_output(remote_command(command),stdin=subprocess.DEVNULL,text=True,encoding='utf-8',timeout=120)

def emit(row):
    row={'tag':args.tag,'time':time.time(),**row}
    with (out/'workloads.jsonl').open('a',encoding='utf-8') as output:output.write(json.dumps(row)+'\n')
    print(json.dumps(row),flush=True)
def send(size,recipient='load@efficiency.invalid',subject='Round2 load',custom=None):
    body=custom if custom is not None else (b'x'*76+b'\r\n')*(size*1024**2//78)+b'tailneedle\r\n'
    subject=str(email.header.Header(subject,'utf-8'))
    # Explicit encoded header avoids relying on the client's SMTPUTF8 support.
    subject=email.header.Header(subject,'utf-8').encode()
    raw=(f'Message-ID: <{uuid.uuid4()}@remote-efficiency.invalid>\r\nFrom: sender@outside.invalid\r\n'
        f'To: {recipient}\r\nSubject: {subject}\r\nMIME-Version: 1.0\r\n'
        'Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\n').encode()+body
    start=time.perf_counter()
    try:
        with smtplib.SMTP(args.host,25,timeout=90) as client:client.sendmail('sender@outside.invalid',recipient,raw)
        return {'ok':True,'seconds':time.perf_counter()-start,'bytes':len(raw)}
    except Exception as error:return {'ok':False,'seconds':time.perf_counter()-start,'error':type(error).__name__,'smtpCode':getattr(error,'smtp_code',None),'smtpReply':getattr(error,'smtp_error',b'').decode(errors='replace')}
def connect():
    connection=imaplib.IMAP4_SSL(args.host,993,ssl_context=tls,timeout=120)
    assert connection.login(credentials['mailAccount'],credentials['mailPassword'])[0]=='OK'
    assert connection.select('INBOX',readonly=True)[0]=='OK'
    return connection
token=api('/auth/token',{'userName':credentials['user'],'password':credentials['password']})['accessToken']
if args.seed:
    try:api('/accounts',{'address':'load@efficiency.invalid','password':credentials['mailPassword'],'quotaBytes':64*1024**3},'POST')
    except HTTPError as error:
        if error.code!=409:raise
    with connect() as connection:
        status,data=connection.uid('SEARCH',None,'ALL')
        assert status=='OK' and not data[0].strip(),'Fixture mailbox must start empty'
    for size in (1,10):
        for repeat in range(2):
            result=send(size,credentials['mailAccount'],f'Round2 {size}MiB 中文 Résumé')
            assert result['ok'],result
    result=send(1,credentials['mailAccount'],'Round2 Unicode',('中文 Résumé ＡＢＣ e\u0301\r\n'*32768).encode())
    assert result['ok'],result
    result=send(0,credentials['mailAccount'],'Round2 empty',b'')
    assert result['ok'],result
    time.sleep(35)
    emit({'event':'fixtures-ready','messages':6})
pid=int(ssh(args.pid_command).strip())
remote=f'/tmp/mailserver-round2/{args.tag}-memory.jsonl'
additional_pid=int(ssh(args.additional_pid_command).strip()) if args.additional_pid_command else 0
monitor=subprocess.Popen(remote_command(
    f'python3 /tmp/mailserver-round2/remote-efficiency-monitor.py --pid {pid} --additional-pid {additional_pid} --client-pid {os.getpid()} --duration {max(600,args.duration_seconds+180)} --output {remote}'),
    stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
atexit.register(monitor.terminate)
for attempt in range(50):
    if ssh('test -s '+remote+' && echo ready || true').strip():break
    if monitor.poll() is not None:raise RuntimeError('Memory collector exited before its first sample')
    time.sleep(.1)
else:raise RuntimeError('Memory collector did not produce a sample')

trace=None;counters=None
if args.trace:
    environment='env DOTNET_ROOT=/tmp/mailserver-round2/runtime DOTNET_ROOT_X64=/tmp/mailserver-round2/runtime DOTNET_ROLL_FORWARD=LatestMajor '
    with (out/(args.tag+'-trace.log')).open('w') as trace_log,(out/(args.tag+'-counters.log')).open('w') as counter_log:
        trace=subprocess.Popen(remote_command(environment+
            f'/tmp/mailserver-round2/dotnet-trace collect -p {pid} --profile dotnet-sampled-thread-time,gc-verbose --buffersize 8 --duration 00:00:01:00 -o /tmp/mailserver-round2/{args.tag}.nettrace'),
            stdin=subprocess.DEVNULL,stdout=trace_log,stderr=subprocess.STDOUT)
        counters=subprocess.Popen(remote_command(environment+
            f'/tmp/mailserver-round2/dotnet-counters collect -p {pid} --counters System.Runtime --format json --duration 00:00:01:10 -o /tmp/mailserver-round2/{args.tag}-counters.json'),
            stdin=subprocess.DEVNULL,stdout=counter_log,stderr=subprocess.STDOUT)
    time.sleep(3)
fetch_queries=('(BODY.PEEK[HEADER])','(BODY.PEEK[])')+ (('(ENVELOPE BODYSTRUCTURE)',) if args.fetch_metadata else ())
started=time.time()
with connect() as connection:
    if args.warmup:
        for size in (1,10):
            with concurrent.futures.ThreadPoolExecutor(max_workers=args.parallelism) as pool:
                warm_results=list(pool.map(lambda _:send(size),range(4)))
            assert all(result['ok'] for result in warm_results),warm_results
        emit({'event':'smtp-warmup-complete'})
        for criterion in ('SUBJECT "definitelyabsent"','SUBJECT "resume"','BODY tailneedle','BODY definitelyabsent'):
            assert connection.uid('SEARCH',None,*criterion.split(' '))[0]=='OK'
        for query in fetch_queries:
            assert connection.uid('FETCH','1:*',query)[0]=='OK'
        emit({'event':'warmup-complete'})
    for round_number in range(1,(10000 if args.duration_seconds else args.rounds)+1):
        round_started=time.time()
        for size in (1,10):
            begin=time.perf_counter()
            client_cpu_begin=time.process_time()
            with concurrent.futures.ThreadPoolExecutor(max_workers=args.parallelism) as pool:results=list(pool.map(lambda _:send(size),range(args.messages_per_size)))
            emit({'operation':'SMTP inbound','round':round_number,'sizeMiB':size,'wallSeconds':time.perf_counter()-begin,'clientCpuSeconds':time.process_time()-client_cpu_begin,'results':results})
            if not args.allow_smtp_rejections:assert all(row['ok'] for row in results),results
        for criterion in ('SUBJECT "definitelyabsent"','SUBJECT "resume"','BODY tailneedle','BODY definitelyabsent'):
            begin=time.perf_counter()
            status,data=connection.uid('SEARCH',None,*criterion.split(' '))
            assert status=='OK'
            emit({'operation':'IMAP SEARCH','round':round_number,'criteria':criterion,'seconds':time.perf_counter()-begin,'matches':data[0].decode()})
            if criterion.startswith('SUBJECT "definitelyabsent"') or criterion=='BODY definitelyabsent':assert not data[0].strip()
            else:assert len(data[0].split())==4,'Fixed fixture search result changed'
        for query in fetch_queries:
            begin=time.perf_counter()
            status,data=connection.uid('FETCH','1:*',query)
            assert status=='OK'
            received=sum(len(row[1]) for row in data if isinstance(row,tuple))
            if query=='(BODY.PEEK[])':assert received==24086070,'Fixed fixture download changed'
            emit({'operation':'IMAP FETCH','round':round_number,'query':query,'seconds':time.perf_counter()-begin,
                'bytes':received})
        emit({'operation':'health','round':round_number,'result':api('/health/ready')})
        samples=[json.loads(line) for line in ssh('tail -n 60 '+remote).splitlines() if line.strip()]
        low=[row for row in samples if row.get('MemAvailableBytes',1<<60)<args.minimum_available_mib*1024**2]
        sustained_low=len(low)>=11 and low[-1]['time']-low[-11]['time']>=9.5 and all(row.get('MemAvailableBytes',1<<60)<args.minimum_available_mib*1024**2 for row in samples[-11:])
        if sustained_low or any(not row.get('alive') or row.get('VmSwapBytes',0)>args.maximum_swap_mib*1024**2 for row in samples):
            emit({'event':'stop','reason':'resource threshold'});raise RuntimeError('Resource guard stopped this run')
        if args.duration_seconds and time.time()-started>=args.duration_seconds:break
        if args.period_seconds:
            time.sleep(max(0,args.period_seconds-(time.time()-round_started)))
if trace:
    trace.wait(timeout=120);counters.wait(timeout=120)
    assert trace.returncode==0 and counters.returncode==0,'Runtime collector failed'
    for suffix in ('.nettrace','-counters.json'):
        if args.local:
            import shutil
            shutil.copyfile('/tmp/mailserver-round2/'+args.tag+suffix,out/(args.tag+suffix))
        else:
            subprocess.run(['scp','-q','-i',args.ssh_key,'root@'+args.host+':/tmp/mailserver-round2/'+args.tag+suffix,str(out/(args.tag+suffix))],check=True)
(out/(args.tag+'-memory.jsonl')).write_text(ssh('cat '+remote),encoding='utf-8')
monitor.terminate()
emit({'event':'complete','durationSeconds':time.time()-started})
