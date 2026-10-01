#!/usr/bin/env python3
"""Owned-fixture protocol burst acceptance. Preparation excluded from 45 minute clock."""
import argparse, base64, concurrent.futures, datetime, email, email.policy, hashlib, http.server, json, os, quopri, shutil, smtplib, socket, socketserver, ssl, struct, subprocess, sys, threading, time, uuid
from pathlib import Path
WORK=Path('/mnt/storage/mailserver-burst-20261001'); ROOT=Path('/mnt/storage/mailserver')
SERVICE='mailserver-evaluation'; RUN='burst-20261001'; MX='mx.burst-20261001.invalid'
ROUNDS=['incoming','outgoing']+[f'{mode}-{n}' for n in range(1,4) for mode in ('fixed','dynamic')]+[f'repeat-{n}' for n in range(1,6)]+['diagnostic']
lock=threading.Lock(); received=[]; connections=0; tls_connections=0; active_round=None; token=None; abort=threading.Event(); temporary_seen=0

def write_json(path,data):path.write_text(json.dumps(data,indent=2))
def private():return json.loads((WORK/'private.json').read_text())
def db():
 sys.path.insert(0,'/tmp/mailserver-round2/python');import pymysql
 return pymysql.connect(host='127.0.0.1',user='app-mailserver',password=private()['databasePassword'],database='app-mailserver',autocommit=True,cursorclass=pymysql.cursors.DictCursor)
def input_path(key):return WORK/'inputs'/(key+'.eml')
def fixture_path(key):return ROOT/'data/messages'/key[:2]/key[2:4]/(key+'.eml')
def prepare():
 WORK.mkdir(parents=True,exist_ok=True); tlsdir=WORK/'tls';tlsdir.mkdir(exist_ok=True)
 if not (tlsdir/'server.pem').exists():
  args=[['openssl','req','-x509','-newkey','rsa:2048','-nodes','-keyout',str(tlsdir/'ca.key'),'-out',str(tlsdir/'ca.pem'),'-days','7','-subj','/CN=Owned Mail Burst Test CA','-addext','basicConstraints=critical,CA:TRUE'],['openssl','req','-new','-newkey','rsa:2048','-nodes','-keyout',str(tlsdir/'server.key'),'-out',str(tlsdir/'server.csr'),'-subj','/CN='+MX]]
  (tlsdir/'server.ext').write_text('subjectAltName=DNS:'+MX+'\nbasicConstraints=CA:FALSE\nkeyUsage=digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\n')
  args.append(['openssl','x509','-req','-in',str(tlsdir/'server.csr'),'-CA',str(tlsdir/'ca.pem'),'-CAkey',str(tlsdir/'ca.key'),'-CAcreateserial','-out',str(tlsdir/'server.pem'),'-days','7','-extfile',str(tlsdir/'server.ext')])
  for command in args:subprocess.run(command,check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
  (tlsdir/'ca-bundle.pem').write_bytes(Path('/etc/ssl/certs/ca-certificates.crt').read_bytes()+(tlsdir/'ca.pem').read_bytes())
  for path in tlsdir.iterdir():path.chmod(0o600)
 (WORK/'controlled-resolv.conf').write_text('nameserver 127.0.0.2\noptions attempts:1 timeout:1\n')
 if not (tlsdir/'ca.crl').exists():prepare_crl()
 manifest=[];sender=private()['mailAccount'];domain=sender.split('@')[1]
 for label in ROUNDS:
  for direction in ('in','out'):
   for number in range(1000):
    identity=f'{RUN}-{label}-{direction}-{number:04d}';mid=identity+'@burst-20261001.invalid';recipient=(f'burst-20261001-{number:04d}@{domain}' if direction=='in' else f'person{number:04d}@d{number%10}.burst-20261001.invalid')
    body=(f'姓名：测试候选人{label}-{direction}-{number:04d}；场景：{identity}；招聘职位：开发工程师。\r\n'.encode('utf8'))
    header=(f'Message-ID: <{mid}>\r\nFrom: {sender}\r\nTo: {recipient}\r\nSubject: =?UTF-8?B?5oub6IGY6YCa55+l?=\r\nX-Burst-Identity: {identity}\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\n').encode()
    # Bound line lengths, preserve CRLF. Final raw is exactly 1 MiB.
    amount=1048576-len(header);content=body+(b'abcdefghij0123456789'*35+b'\r\n')*((amount-len(body))//702)
    content=content[:amount-2]+b'X'*(amount-2-len(content))+b'\r\n';raw=header+content
    assert len(raw)==1048576 and max(map(len,raw.split(b'\r\n')))<1000
    key=hashlib.sha256(raw).hexdigest();path=input_path(key) if direction=='in' else fixture_path(key);path.parent.mkdir(parents=True,exist_ok=True)
    if not path.exists():path.write_bytes(raw)
    manifest.append({'round':label,'direction':direction,'mid':mid,'recipient':recipient,'key':key,'bodyHash':hashlib.sha256(content).hexdigest(),'id':str(uuid.uuid4()),'jobId':str(uuid.uuid4())})
  print(json.dumps({'prepared':label}),flush=True)
 prepare_probes(manifest)
 prepare_aliases()

def prepare_probes(manifest=None):
 if manifest is None:manifest=json.loads((WORK/'manifest.json').read_text())
 if any(r['round']=='preflight' for r in manifest):
  assert len([r for r in manifest if r['round']=='preflight'])==3
  return
 for index,kind in enumerate(('success','temporary','permanent')):
  template=next(r for r in manifest if r['round']=='outgoing' and r['direction']=='out' and r['recipient'].startswith('person'+f'{index:04d}'))
  oldraw=fixture_path(template['key']).read_bytes();header,body=oldraw.split(b'\r\n\r\n',1);mid=RUN+'-preflight-'+kind+'@burst-20261001.invalid';recipient=kind+'@d0.burst-20261001.invalid'
  header=header.replace(template['mid'].encode(),mid.encode()).replace(template['recipient'].encode(),recipient.encode());amount=1048576-len(header)-4;body=body[:amount-2]+b'X'*max(0,amount-2-len(body))+b'\r\n';raw=header+b'\r\n\r\n'+body;assert len(raw)==1048576
  key=hashlib.sha256(raw).hexdigest();path=fixture_path(key);path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(raw)
  manifest.append({'round':'preflight','direction':'out','mid':mid,'recipient':recipient,'key':key,'bodyHash':hashlib.sha256(body).hexdigest(),'id':str(uuid.uuid4()),'jobId':str(uuid.uuid4()),'sender':private()['mailAccount']})
 write_json(WORK/'manifest.json',manifest)

def normalize_probe_senders():
 manifest=json.loads((WORK/'manifest.json').read_text());rows=[r for r in manifest if r['round']=='preflight'];assert len(rows)==3
 for row in rows:row['sender']=private()['mailAccount']
 write_json(WORK/'manifest.json',manifest)

def prepare_crl():
 tlsdir=WORK/'tls';tlsdir.mkdir(exist_ok=True)
 crl_uri='http://127.0.0.2:8082/ca.crl'
 commands=[['openssl','req','-x509','-newkey','rsa:2048','-nodes','-keyout',str(tlsdir/'ca.key'),'-out',str(tlsdir/'ca.pem'),'-days','7','-subj','/CN=Owned Mail Burst Test CA','-addext','basicConstraints=critical,CA:TRUE','-addext','keyUsage=critical,keyCertSign,cRLSign','-addext','crlDistributionPoints=URI:'+crl_uri],['openssl','req','-new','-newkey','rsa:2048','-nodes','-keyout',str(tlsdir/'server.key'),'-out',str(tlsdir/'server.csr'),'-subj','/CN='+MX]]
 (tlsdir/'server.ext').write_text('subjectAltName=DNS:'+MX+'\nbasicConstraints=CA:FALSE\nkeyUsage=digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\ncrlDistributionPoints=URI:'+crl_uri+'\n')
 commands.append(['openssl','x509','-req','-in',str(tlsdir/'server.csr'),'-CA',str(tlsdir/'ca.pem'),'-CAkey',str(tlsdir/'ca.key'),'-CAcreateserial','-out',str(tlsdir/'server.pem'),'-days','7','-extfile',str(tlsdir/'server.ext')])
 (tlsdir/'crl-index.txt').write_text('');(tlsdir/'crl-number.txt').write_text('1000\n');(tlsdir/'serial.txt').write_text('1000\n');(tlsdir/'newcerts').mkdir(exist_ok=True)
 configuration='[ca]\ndefault_ca=owned\n[owned]\ndir='+str(tlsdir)+'\ndatabase=$dir/crl-index.txt\nnew_certs_dir=$dir/newcerts\ncertificate=$dir/ca.pem\nprivate_key=$dir/ca.key\nserial=$dir/serial.txt\ncrlnumber=$dir/crl-number.txt\ndefault_md=sha256\ndefault_days=7\ndefault_crl_days=7\npolicy=policy_owned\n[policy_owned]\ncommonName=supplied\n'
 (tlsdir/'crl.cnf').write_text(configuration)
 commands.extend([['openssl','ca','-config',str(tlsdir/'crl.cnf'),'-gencrl','-out',str(tlsdir/'ca-crl.pem')],['openssl','crl','-in',str(tlsdir/'ca-crl.pem'),'-outform','DER','-out',str(tlsdir/'ca.crl')]])
 for command in commands:subprocess.run(command,check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
 (tlsdir/'ca-bundle.pem').write_bytes(Path('/etc/ssl/certs/ca-certificates.crt').read_bytes()+(tlsdir/'ca.pem').read_bytes())
 for path in tlsdir.iterdir():path.chmod(0o700 if path.is_dir() else 0o600)
 subprocess.run(['openssl','verify','-CAfile',str(tlsdir/'ca.pem'),'-CRLfile',str(tlsdir/'ca-crl.pem'),'-crl_check_all','-verify_hostname',MX,str(tlsdir/'server.pem')],check=True,stdout=subprocess.DEVNULL)
 write_json(WORK/'crl-preparation.json',{'crlDistributionPoint':crl_uri,'opensslChainAndHostnameAndFullRevocationCheck':True,'revokedEntries':0,'caSha256':hashlib.sha256((tlsdir/'ca.pem').read_bytes()).hexdigest(),'crlSha256':hashlib.sha256((tlsdir/'ca.crl').read_bytes()).hexdigest()})

def relocate_inputs():
 manifest=json.loads((WORK/'manifest.json').read_text());rows=[r for r in manifest if r['direction']=='in' and not r.get('cleanupOnly')];moved=0
 with db() as conn,conn.cursor() as q:
  for offset in range(0,len(rows),250):
   batch=rows[offset:offset+250];q.execute('SELECT ContentKey FROM stored_messages WHERE ContentKey IN ('+','.join(['%s']*len(batch))+')',[r['key'] for r in batch]);assert not q.fetchall(),'Cannot relocate content already referenced by database'
   for row in batch:
    target=input_path(row['key']);target.parent.mkdir(parents=True,exist_ok=True);source=fixture_path(row['key'])
    if source.exists():assert not target.exists();os.replace(source,target);moved+=1
    assert target.exists() and target.stat().st_size==1048576
 write_json(WORK/'input-relocation.json',{'moved':moved,'inputCount':len(rows),'noPreexistingInboundContent':True})

def warm_service():
 from urllib.request import Request,urlopen
 credentials=private();payload=json.dumps({'userName':credentials['user'],'password':credentials['password']}).encode()
 with urlopen(Request('https://127.0.0.1:5000/api/v1/auth/token',data=payload,headers={'Content-Type':'application/json'}),context=client_tls,timeout=10) as response:access=json.load(response)['accessToken']
 until=time.monotonic()+10
 while time.monotonic()<until:
  for path in ('/accounts','/domains'):
   with urlopen(Request('https://127.0.0.1:5000/api/v1'+path,headers={'Authorization':'Bearer '+access}),context=client_tls,timeout=10) as response:response.read()
  time.sleep(.5)

def prepare_aliases():
 now=datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None);owned=[];address=private().get('loadAccount','load@efficiency.invalid');local,domain=address.split('@')
 with db() as conn,conn.cursor() as q:
  q.execute('SELECT a.Id,a.DomainId FROM mail_accounts a JOIN mail_domains d ON d.Id=a.DomainId WHERE a.LocalPart=%s AND d.Name=%s',(local,domain));account=q.fetchone();assert account,'Missing owned load account'
  conn.begin()
  try:
   for number in range(1000):
    alias='burst-20261001-'+f'{number:04d}';q.execute('SELECT Id FROM mail_aliases WHERE DomainId=%s AND SourceLocalPart=%s',(account['DomainId'],alias));assert q.fetchone() is None,'Refuse overwrite of existing alias'
    ident=str(uuid.uuid4());q.execute('INSERT INTO mail_aliases (Id,DomainId,SourceLocalPart,TargetAccountId,Mode,IsEnabled,CreatedAt) VALUES (%s,%s,%s,%s,%s,1,%s)',(ident,account['DomainId'],alias,account['Id'],'ReceiveOnly',now));owned.append(ident)
   conn.commit()
  except BaseException:conn.rollback();raise
 write_json(WORK/'owned-aliases.json',owned)
 # Restart before the formal clock invalidates the routing cache.

def cleanup():
 subprocess.run(['systemctl','stop',SERVICE],check=True)
 manifest=json.loads((WORK/'manifest.json').read_text());mids=[r['mid'] for r in manifest];released=0;deleted=0
 with db() as conn,conn.cursor() as q:
  for offset in range(0,len(mids),250):
   batch=mids[offset:offset+250];ph=','.join(['%s']*len(batch));conn.begin()
   try:
    q.execute('SELECT s.Id,s.ContentKey FROM stored_messages s WHERE s.InternetMessageId IN ('+ph+')',batch);messages=q.fetchall();ids=[r['Id'] for r in messages]
    if not ids:conn.rollback();continue
    iph=','.join(['%s']*len(ids))
    q.execute('SELECT m.Id,m.QuotaReleased,s.Size,b.AccountId,a.DomainId FROM mailbox_messages m JOIN stored_messages s ON s.Id=m.MessageId JOIN mailboxes b ON b.Id=m.MailboxId JOIN mail_accounts a ON a.Id=b.AccountId WHERE m.MessageId IN ('+iph+')',ids);refs=q.fetchall()
    for domain in sorted({r['DomainId'] for r in refs}):q.execute('SELECT Id FROM mail_domains WHERE Id=%s FOR UPDATE',(domain,));q.fetchone()
    for account in sorted({r['AccountId'] for r in refs}):q.execute('SELECT Id FROM mail_accounts WHERE Id=%s FOR UPDATE',(account,));q.fetchone()
    for r in refs:
     if not r['QuotaReleased']:
      q.execute('INSERT INTO quota_ledger (Id,IdempotencyKey,AccountId,DomainId,MailboxMessageId,Operation,Bytes,CreatedAt) VALUES (%s,%s,%s,%s,%s,%s,%s,UTC_TIMESTAMP())',(str(uuid.uuid4()),RUN+'-cleanup:'+r['Id'],r['AccountId'],r['DomainId'],r['Id'],'Remove',-r['Size']))
      q.execute('UPDATE mail_accounts SET UsedBytes=UsedBytes-%s WHERE Id=%s AND UsedBytes>=%s',(r['Size'],r['AccountId'],r['Size']));assert q.rowcount==1
      q.execute('UPDATE mail_domains SET UsedBytes=UsedBytes-%s WHERE Id=%s AND UsedBytes>=%s',(r['Size'],r['DomainId'],r['Size']));assert q.rowcount==1;released+=r['Size']
     q.execute("DELETE FROM cleanup_queue WHERE ResourceType='MailboxMessage' AND ResourceId=%s",(r['Id'],))
    q.execute('DELETE FROM mailbox_messages WHERE MessageId IN ('+iph+')',ids)
    q.execute('DELETE FROM sent_copy_jobs WHERE MessageId IN ('+iph+')',ids)
    q.execute('DELETE FROM delivery_jobs WHERE MessageId IN ('+iph+')',ids)
    q.execute('DELETE FROM quarantine_records WHERE MessageId IN ('+iph+')',ids)
    q.execute('DELETE FROM stored_messages WHERE Id IN ('+iph+')',ids);deleted+=q.rowcount;conn.commit()
    for row in messages:
     q.execute('SELECT COUNT(*) AS count FROM stored_messages WHERE ContentKey=%s',(row['ContentKey'],))
     if q.fetchone()['count']==0:fixture_path(row['ContentKey']).unlink(missing_ok=True)
   except BaseException:conn.rollback();raise
  owned=json.loads((WORK/'owned-aliases.json').read_text()) if (WORK/'owned-aliases.json').exists() else []
  if owned:q.execute('DELETE FROM mail_aliases WHERE Id IN ('+','.join(['%s']*len(owned))+')',owned)
  for row in manifest:
   q.execute('SELECT COUNT(*) AS count FROM stored_messages WHERE ContentKey=%s',(row['key'],))
   if q.fetchone()['count']==0:fixture_path(row['key']).unlink(missing_ok=True)
   if row['direction']=='in':input_path(row['key']).unlink(missing_ok=True)
 write_json(WORK/'cleanup.json',{'messagesDeleted':deleted,'quotaReleased':released,'ownedAliasCount':len(owned),'auditAndLedgerRetained':True})

class ThreadTcp(socketserver.ThreadingTCPServer):allow_reuse_address=True;daemon_threads=True

def name_wire(value):return b''.join(bytes([len(p)])+p.encode() for p in value.split('.'))+b'\0'
def dns_answer(raw):
 pos=12;parts=[]
 while raw[pos]:n=raw[pos];parts.append(raw[pos+1:pos+1+n].decode());pos+=n+1
 pos+=1;typ,_=struct.unpack('!HH',raw[pos:pos+4]);question=raw[12:pos+4];name='.'.join(parts).lower();payload=None
 if name.startswith('d') and name.endswith('.burst-20261001.invalid') and typ==15:payload=struct.pack('!H',10)+name_wire(MX)
 elif name==MX and typ==1:payload=socket.inet_aton('127.0.0.2')
 rr=b'\xc0\x0c'+struct.pack('!HHIH',typ,1,30,len(payload))+payload if payload else b''
 return raw[:2]+struct.pack('!HHHHH',0x8180,1,int(bool(rr)),0,0)+question+rr
class DnsUdp(socketserver.BaseRequestHandler):
 def handle(self):
  raw,sock=self.request;sock.sendto(dns_answer(raw),self.client_address)
class DnsTcp(socketserver.StreamRequestHandler):
 def handle(self):
  while True:
   length=self.rfile.read(2)
   if len(length)!=2:return
   raw=self.rfile.read(struct.unpack('!H',length)[0]);answer=dns_answer(raw);self.wfile.write(struct.pack('!H',len(answer))+answer);self.wfile.flush()
class CrlHttp(http.server.BaseHTTPRequestHandler):
 def do_GET(self):
  if self.path!='/ca.crl':self.send_error(404);return
  payload=(WORK/'tls/ca.crl').read_bytes();self.send_response(200);self.send_header('Content-Type','application/pkix-crl');self.send_header('Content-Length',str(len(payload)));self.end_headers();self.wfile.write(payload)
 def log_message(self,*args):pass

class Sink(socketserver.StreamRequestHandler):
 def handle(self):
  global connections,tls_connections,temporary_seen
  self.connection.settimeout(120);recipient='';encrypted=False
  with lock:connections+=1
  def reply(raw):self.wfile.write(raw+b'\r\n');self.wfile.flush()
  reply(b'220 owned burst sink')
  while True:
   line=self.rfile.readline()
   if not line:return
   command=line.decode(errors='replace').strip();verb=command.split(' ',1)[0].upper()
   if verb in ('EHLO','HELO'):reply(b'250-burst sink\r\n250-SIZE 52428800\r\n'+(b'250 STARTTLS' if not encrypted else b'250 8BITMIME'))
   elif verb=='STARTTLS':
    reply(b'220 Begin TLS');self.connection=sink_tls.wrap_socket(self.connection,server_side=True);self.rfile=self.connection.makefile('rb');self.wfile=self.connection.makefile('wb');encrypted=True
    with lock:tls_connections+=1
   elif verb=='MAIL':reply(b'250 sender accepted' if encrypted else b'530 TLS required')
   elif verb=='RCPT':
    recipient=command.split(':',1)[1].strip('<> ');local=recipient.split('@')[0]
    if local=='permanent':reply(b'550 5.1.1 owned permanent rejection')
    elif local=='temporary':
     with lock:temporary_seen+=1;reject=temporary_seen==1
     reply(b'451 4.3.0 owned temporary rejection' if reject else b'250 recipient accepted')
    else:reply(b'250 recipient accepted')
   elif verb=='DATA':
    reply(b'354 send data');header=bytearray();body_hash=hashlib.sha256();in_body=False;size=0;transfer='8bit'
    while True:
     raw=self.rfile.readline()
     if raw==b'.\r\n':break
     if not raw:return
     raw=raw[1:] if raw.startswith(b'..') else raw;size+=len(raw)
     if in_body:
      decoded=quopri.decodestring(raw) if transfer=='quoted-printable' else base64.b64decode(raw) if transfer=='base64' else raw
      body_hash.update(decoded)
     else:
      header.extend(raw)
      if raw==b'\r\n':
       in_body=True;transfer=str(email.message_from_bytes(bytes(header),policy=email.policy.default).get('Content-Transfer-Encoding','8bit')).lower()
    parsed=email.message_from_bytes(bytes(header),policy=email.policy.default);now=time.time();record={'mid':str(parsed['Message-ID']).strip('<>'),'recipient':recipient,'size':size,'bodyHash':body_hash.hexdigest(),'receivedAt':now,'tls':encrypted}
    with lock:received.append(record)
    reply(b'250 2.0.0 committed');record['ackAt']=time.time()
   elif verb in ('RSET','NOOP'):reply(b'250 ready')
   elif verb=='QUIT':reply(b'221 bye');return
   else:reply(b'502 unsupported')

def resources():
 pid=int(subprocess.check_output(['systemctl','show','-p','MainPID','--value',SERVICE]));cg=Path('/sys/fs/cgroup'+subprocess.check_output(['systemctl','show','-p','ControlGroup','--value',SERVICE],text=True).strip())
 def read(path):
  try:return Path(path).read_text()
  except (OSError,ProcessLookupError):return None
 roles={'app':pid,'clientSink':os.getpid()}
 try:roles['mysql']=int(subprocess.check_output(['docker','inspect','-f','{{.State.Pid}}','1Panel-mysql']))
 except Exception:pass
 return {'timestamp':time.time(),'round':active_round,'host':{n:read('/proc/'+n) for n in ('stat','meminfo','diskstats','net/dev','pressure/cpu','pressure/memory','pressure/io')},'cgroup':{n:read(cg/n) for n in ('cpu.stat','memory.current','memory.stat','memory.events','cpu.pressure','memory.pressure','io.pressure')},'processes':{role:{'pid':value,**{n:read(f'/proc/{value}/{n}') for n in ('stat','status','io')}} for role,value in roles.items()}}
def monitor(stop):
 low_since=None;initial_pid=None;initial_oom=None;health_failures=0;sample_number=0;monitor_since=datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%d %H:%M:%S UTC')
 from urllib.request import urlopen
 with (WORK/'resources.jsonl').open('a') as output:
  while not stop.is_set():
   sample=resources();output.write(json.dumps(sample)+'\n');output.flush();sample_number+=1
   pid=sample['processes']['app']['pid'];initial_pid=initial_pid or pid
   memory_current=sample['cgroup']['memory.current']
   current=int(memory_current) if memory_current else None
   events=dict(line.split() for line in (sample['cgroup']['memory.events'] or '').splitlines());oom=int(events.get('oom_kill',0));initial_oom=oom if initial_oom is None else initial_oom
   if current is not None and 4*1024**3-current<128*1024**2:low_since=low_since or time.monotonic()
   else:low_since=None
   reason=None
   if oom>initial_oom:reason='OOM kill'
   elif low_since and time.monotonic()-low_since>=5:reason='application memory headroom below 128 MiB for 5 seconds'
   # Mode changes intentionally restart the process; each round records the initial PID separately.
   if sample_number%5==0 and active_round:
    try:
     with urlopen('https://127.0.0.1:5000/api/v1/health/ready',context=client_tls,timeout=2) as response:
      if response.status!=200:raise RuntimeError('health')
     health_failures=0
    except Exception:health_failures+=1
    if health_failures>=3:reason='health failed three times'
    try:
     marker=subprocess.run(['journalctl','-u',SERVICE,'--since',monitor_since,'-g','adaptive-experiment-stop-requested','-n','1','--no-pager','-o','cat'],capture_output=True,text=True,timeout=2)
     if 'adaptive-experiment-stop-requested' in marker.stdout:reason='adaptive controller requested safety stop'
    except (OSError,subprocess.TimeoutExpired):pass
   if reason:
    write_json(WORK/'safety-stop.json',{'time':time.time(),'round':active_round,'reason':reason});abort.set()
   stop.wait(1)
def enqueue(rows):
 now=datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None);future=now+datetime.timedelta(days=2)
 with db() as conn,conn.cursor() as q:
  conn.begin()
  try:
   for r in rows:
    q.execute('INSERT INTO stored_messages (Id,ContentKey,EnvelopeFrom,InternetMessageId,InternetMessageIdHash,SourceType,Size,ReceivedAt) VALUES (%s,%s,%s,%s,%s,%s,%s,%s)',(r['id'],r['key'],r.get('sender',private()['mailAccount']),r['mid'],hashlib.sha256(r['mid'].encode()).hexdigest(),'burst-fixture',1048576,now))
    q.execute('INSERT INTO stored_message_recipients (Id,MessageId,Address) VALUES (%s,%s,%s)',(str(uuid.uuid4()),r['id'],r['recipient']))
    q.execute('INSERT INTO delivery_jobs (Id,MessageId,Sender,Recipient,Status,Attempts,NextAttemptAt,ForwardDepth,CreatedAt,UpdatedAt) VALUES (%s,%s,%s,%s,%s,0,%s,0,%s,%s)',(r['jobId'],r['id'],r.get('sender',private()['mailAccount']),r['recipient'],'Pending',future,now,now))
   conn.commit()
  except BaseException:conn.rollback();raise

def incoming(row,t0,hard_stop):
 started=time.time();attempts=[]
 while not abort.is_set() and time.time()<t0+30 and time.monotonic()<hard_stop:
  try:
   with smtplib.SMTP('127.0.0.1',25,timeout=10) as client:
    client.starttls(context=client_tls);client.sendmail('sender@burst-20261001.invalid',row['recipient'],input_path(row['key']).read_bytes());ack=time.time()
   return {'mid':row['mid'],'ok':True,'completedAt':ack,'elapsed':ack-started,'attempts':attempts}
  except smtplib.SMTPRecipientsRefused as exc:
   codes=[value[0] for value in exc.recipients.values()];attempts.append({'at':time.time(),'errorType':type(exc).__name__,'smtpCodes':codes})
   if not codes or any(code not in (421,451) for code in codes):break
   time.sleep(.05)
  except smtplib.SMTPResponseException as exc:
   attempts.append({'at':time.time(),'errorType':type(exc).__name__,'smtpCode':exc.smtp_code})
   # Explicit negative acknowledgement is safe to retry. Disconnect/timeouts after DATA are not.
   if exc.smtp_code not in (421,451):break
   time.sleep(.05)
  except Exception as exc:
   attempts.append({'at':time.time(),'errorType':type(exc).__name__,'smtpCode':getattr(exc,'smtp_code',None),'unknownDeliveryOutcome':isinstance(exc,(smtplib.SMTPServerDisconnected,TimeoutError))});break
 return {'mid':row['mid'],'ok':False,'notStarted':not attempts,'completedAt':time.time(),'elapsed':time.time()-started,'attempts':attempts}
def queue_state(rows):
 if not rows:return {}
 with db() as conn,conn.cursor() as q:
  q.execute('SELECT Status,COUNT(*) AS count FROM delivery_jobs WHERE Id IN ('+','.join(['%s']*len(rows))+') GROUP BY Status',[r['jobId'] for r in rows]);return {r['Status']:r['count'] for r in q.fetchall()}
def discover_probe_bounces(manifest=None,since=None):
 if manifest is None:manifest=json.loads((WORK/'manifest.json').read_text())
 permanent=next(r for r in manifest if r['round']=='preflight' and r['recipient'].startswith('permanent@'));job=permanent['jobId'].replace('-','')
 if since is None:
  marker=WORK/'initial-environment.json';since=datetime.datetime.fromtimestamp(marker.stat().st_mtime,datetime.timezone.utc).replace(tzinfo=None)
 elif isinstance(since,(float,int)):since=datetime.datetime.fromtimestamp(since,datetime.timezone.utc).replace(tzinfo=None)
 with db() as conn,conn.cursor() as q:
  q.execute("SELECT DISTINCT s.Id,s.InternetMessageId,s.ContentKey,s.Size FROM stored_messages s JOIN stored_message_recipients r ON r.MessageId=s.Id WHERE s.SourceType='bounce' AND s.ReceivedAt>=%s AND r.Address=%s",(since,private()['mailAccount']));candidates=q.fetchall()
 owned=[]
 for candidate in candidates:
  parsed=email.message_from_bytes(fixture_path(candidate['ContentKey']).read_bytes(),policy=email.policy.default)
  if str(parsed.get('Auto-Submitted','')).lower()!='auto-replied':continue
  text=' '.join(str(part.get_content()) for part in parsed.walk() if part.get_content_maintype()=='text')
  if job not in text and not (permanent['recipient'] in text and 'owned permanent rejection' in text):continue
  owned.append(candidate)
  if not any(row['id']==candidate['Id'] for row in manifest):manifest.append({'round':'preflight-bounce','direction':'in','mid':candidate['InternetMessageId'],'recipient':private()['mailAccount'],'key':candidate['ContentKey'],'size':candidate['Size'],'id':candidate['Id'],'cleanupOnly':True})
 if owned:write_json(WORK/'manifest.json',manifest)
 return owned

def preflight(manifest):
 rows=[r for r in manifest if r['round']=='preflight'];assert len(rows)==3
 prior=WORK/'preflight.json'
 if prior.exists():
  proof=json.loads(prior.read_text())
  assert proof.get('artifactSha256')==hashlib.sha256((ROOT/'mailserver').read_bytes()).hexdigest(),'Preflight artifact changed'
  with db() as conn,conn.cursor() as q:
   q.execute('SELECT Id,Status,Attempts FROM delivery_jobs WHERE Id IN (%s,%s,%s)',[r['jobId'] for r in rows]);existing={r['Id']:r for r in q.fetchall()}
  assert len(existing)==3 and [existing[r['jobId']]['Status'] for r in rows]==['Delivered','Delivered','Failed']
  return
 subprocess.run(['python3',str(WORK/'remote_burst_setup.py'),'fixed'],check=True)
 warm_service()
 probe_started=datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None)
 with smtplib.SMTP('127.0.0.1',25,timeout=10) as warm:
  warm.starttls(context=client_tls);warm.ehlo();assert warm.mail('sender@burst-20261001.invalid')[0]==250
  for row in [r for r in manifest if r['round']=='incoming' and r['direction']=='in'][:3]:assert warm.rcpt(row['recipient'])[0]==250,'Owned inbound aliases do not resolve'
  warm.rset()
 enqueue(rows)
 ids=[r['jobId'] for r in rows]
 with db() as conn,conn.cursor() as q:q.execute('UPDATE delivery_jobs SET NextAttemptAt=UTC_TIMESTAMP() WHERE Id IN (%s,%s,%s)',ids)
 cutoff=time.monotonic()+90;retried=False;states=[]
 while time.monotonic()<cutoff:
  with db() as conn,conn.cursor() as q:
   q.execute('SELECT Id,Status,Attempts FROM delivery_jobs WHERE Id IN (%s,%s,%s)',ids);states=q.fetchall();byid={r['Id']:r for r in states};temporary=byid[rows[1]['jobId']]
   if temporary['Status']=='Deferred' and temporary['Attempts']==1 and not retried:
    q.execute("UPDATE delivery_jobs SET NextAttemptAt=UTC_TIMESTAMP() WHERE Id=%s AND Status='Deferred' AND Attempts=1",(temporary['Id'],));retried=True
  if byid[rows[0]['jobId']]['Status']=='Delivered' and byid[rows[1]['jobId']]['Status']=='Delivered' and byid[rows[2]['jobId']]['Status']=='Failed':break
  time.sleep(.2)
 assert [byid[r['jobId']]['Status'] for r in rows]==['Delivered','Delivered','Failed'],'Controlled failure preflight did not reach final statuses'
 assert retried and len(states)==3 and byid[rows[0]['jobId']]['Attempts']==1 and byid[rows[1]['jobId']]['Attempts']==2 and byid[rows[2]['jobId']]['Attempts']==1,'Controlled TLS and failure preflight failed'
 with lock:sink=[dict(r) for r in received if r['mid'] in {row['mid'] for row in rows}]
 assert len(sink)==2 and all(r['tls'] for r in sink)
 expected={r['mid']:r for r in rows};assert all(r['bodyHash']==expected[r['mid']]['bodyHash'] for r in sink),'Serialized body differs from fixture'
 write_json(WORK/'preflight-partial.json',{'states':states,'received':sink,'temporaryRetryConfirmed':retried,'allTls':True,'bodyVerified':True,'probeStarted':probe_started.isoformat() if isinstance(probe_started,datetime.datetime) else probe_started,'artifactSha256':hashlib.sha256((ROOT/'mailserver').read_bytes()).hexdigest()})
 # Rendered templates may omit queueId; require the unique controlled recipient and exact rejection together.
 bounce_deadline=time.monotonic()+40;owned_bounces=[]
 while time.monotonic()<bounce_deadline and not owned_bounces:
  owned_bounces=discover_probe_bounces(manifest,probe_started)
  if not owned_bounces:time.sleep(.2)
 assert len(owned_bounces)<=1,'Multiple matching current-probe DSNs require review'
 bounce=owned_bounces[0] if owned_bounces else None
 write_json(WORK/'preflight.json',{'states':states,'received':sink,'temporaryRetryConfirmed':retried,'allTls':True,'bodyVerified':True,'controlledBounceMessageId':bounce['Id'] if bounce else None,'controlledBounceInternetMessageId':bounce['InternetMessageId'] if bounce else None,'bounceVerified':bool(bounce),'bounceLimitation':None if bounce else 'No DSN observed; final permanent rejection verified. Notification generation remains unverified.','artifactSha256':hashlib.sha256((ROOT/'mailserver').read_bytes()).hexdigest()})

def finalize_preflight():
 manifest=json.loads((WORK/'manifest.json').read_text());partial=json.loads((WORK/'preflight-partial.json').read_text());rows=[r for r in manifest if r['round']=='preflight'];sha=hashlib.sha256((ROOT/'mailserver').read_bytes()).hexdigest()
 assert partial['artifactSha256']==sha and partial['bodyVerified'] and partial['allTls'],'Partial proof does not match current artifact'
 expected={r['mid']:r for r in rows};received=partial['received'];assert len(received)==2 and len({r['mid'] for r in received})==2
 assert all(r['tls'] and r['bodyHash']==expected[r['mid']]['bodyHash'] and r['recipient']==expected[r['mid']]['recipient'] for r in received)
 with db() as conn,conn.cursor() as q:
  q.execute('SELECT Id,Status,Attempts FROM delivery_jobs WHERE Id IN (%s,%s,%s)',[r['jobId'] for r in rows]);states=q.fetchall();byid={r['Id']:r for r in states}
 assert len(states)==3 and [byid[r['jobId']]['Status'] for r in rows]==['Delivered','Delivered','Failed'] and [byid[r['jobId']]['Attempts'] for r in rows]==[1,2,1]
 bounces=discover_probe_bounces(manifest);partial.update({'states':states,'bounceVerified':bool(bounces),'controlledBounceIds':[r['Id'] for r in bounces],'bounceLimitation':None if bounces else 'No DSN rows or notification logs observed; permanent SMTP rejection verified, notification generation remains unverified.','finalizedFromPartial':True})
 write_json(WORK/'preflight.json',partial)
 print(json.dumps({'tlsVerified':True,'bodyVerified':True,'actualJobStatesVerified':True,'bounceVerified':bool(bounces),'artifactSha256':sha}),flush=True)

def run_round(label,mode,deadline,manifest):
 global active_round
 if time.monotonic()+95>deadline:return {'round':label,'skipped':'stage budget'}
 active_round=label;ins=[r for r in manifest if r['round']==label and r['direction']=='in'] if label!='outgoing' else [];outs=[r for r in manifest if r['round']==label and r['direction']=='out'] if label!='incoming' else []
 enqueue(outs);active_round=None;subprocess.run(['python3',str(WORK/'remote_burst_setup.py'),mode],check=True)
 warm_service()
 # Warm inbound TLS/routing without DATA, and never connect the outbound sink before T0.
 with smtplib.SMTP('127.0.0.1',25,timeout=10) as warm:
  warm.starttls(context=client_tls);warm.ehlo();assert warm.mail('sender@burst-20261001.invalid')[0]==250
  for r in [x for x in manifest if x['round']==label and x['direction']=='in'][:3]:assert warm.rcpt(r['recipient'])[0]==250
  warm.rset()
 active_round=label
 # Mode hook restarts the app, empties SMTP pool and returns only after health readiness.
 if label=='diagnostic':subprocess.run(['python3',str(WORK/'remote_burst_diagnostic.py'),'start'],check=True)
 with lock:connection_start=connections;tls_start=tls_connections
 round_pid=int(subprocess.check_output(['systemctl','show','-p','MainPID','--value',SERVICE]));mids={r['mid'] for r in outs};start=time.time();t0=time.monotonic();hard_stop=min(deadline,t0+90);events=[]
 if outs:
  with db() as conn,conn.cursor() as q:q.execute('UPDATE delivery_jobs SET NextAttemptAt=UTC_TIMESTAMP(),UpdatedAt=UTC_TIMESTAMP() WHERE Id IN ('+','.join(['%s']*len(outs))+') AND Status=%s AND Attempts=0',[r['jobId'] for r in outs]+['Pending'])
 # Database polling remains the cross-process wakeup fallback, included in completion time.
 pool=concurrent.futures.ThreadPoolExecutor(max_workers=64);futures=[pool.submit(incoming,r,start,hard_stop) for r in ins];drained=False;curve=[]
 while not abort.is_set() and time.monotonic()<hard_stop:
  current_pid=int(subprocess.check_output(['systemctl','show','-p','MainPID','--value',SERVICE]));
  if current_pid!=round_pid:
   write_json(WORK/'safety-stop.json',{'time':time.time(),'round':label,'reason':'unexpected service restart during round'});abort.set();break
  done=[f.result() for f in futures if f.done()];state=queue_state(outs)
  with lock:sink=[dict(r) for r in received if r['mid'] in mids]
  curve.append({'elapsed':time.monotonic()-t0,'incomingAck':sum(r['ok'] for r in done),'sinkReceived':len(sink),'queue':state})
  if all(f.done() for f in futures) and (not outs or state.get('Delivered',0)+state.get('Failed',0)==len(outs)):drained=True;break
  time.sleep(1)
 pool.shutdown(wait=True,cancel_futures=True);measurement_elapsed=time.monotonic()-t0;events=[f.result() for f in futures if f.done() and not f.cancelled()]
 with lock:sink=[dict(r) for r in received if r['mid'] in mids];connection_delta=connections-connection_start;tls_delta=tls_connections-tls_start
 expected={r['mid']:r for r in outs};bad=[r['mid'] for r in sink if r['recipient']!=expected[r['mid']]['recipient'] or r['bodyHash']!=expected[r['mid']]['bodyHash'] or not r['tls']]
 with db() as conn,conn.cursor() as q:
  incomingmids=[r['mid'] for r in ins]
  incoming_records=[];reference_count=0;incoming_content_failures=[]
  if incomingmids:
   q.execute('SELECT s.InternetMessageId,s.ContentKey,s.Size,m.Id AS ReferenceId FROM stored_messages s JOIN mailbox_messages m ON m.MessageId=s.Id WHERE s.InternetMessageId IN ('+','.join(['%s']*len(incomingmids))+') AND m.DeletedAt IS NULL',incomingmids);incoming_records=q.fetchall();reference_count=len(incoming_records)
   persisted=len({r['InternetMessageId'] for r in incoming_records});incoming_expected={r['mid']:r for r in ins}
   incoming_content_failures=[r['InternetMessageId'] for r in incoming_records if r['ContentKey']!=incoming_expected[r['InternetMessageId']]['key'] or r['Size']!=1048576]
  else:persisted=0
 result={'round':label,'mode':mode,'t0':start,'drained':drained,'elapsed':measurement_elapsed,'incoming30':sum(r['ok'] and r.get('completedAt',1e99)<=start+30 for r in events),'outgoing30':sum(r.get('ackAt',1e99)<=start+30 for r in sink),'incomingPersisted':persisted,'incomingReferenceCount':reference_count,'incomingDuplicateReferences':reference_count-persisted,'incomingContentFailures':incoming_content_failures,'sinkConnections':connection_delta,'sinkTlsHandshakes':tls_delta,'outgoingUnique':len({r['mid'] for r in sink}),'duplicates':len(sink)-len({r['mid'] for r in sink}),'contentFailures':bad,'queue':queue_state(outs),'curve':curve,'incoming':events,'received':sink}
 if label=='diagnostic':subprocess.run(['python3',str(WORK/'remote_burst_diagnostic.py'),'stop'],check=False)
 write_json(WORK/(label+'.json'),result);print(json.dumps({k:v for k,v in result.items() if k not in ('incoming','received','curve','contentFailures')}),flush=True)
 return result

def execute(probe_only=False):
 global sink_tls,client_tls
 manifest=json.loads((WORK/'manifest.json').read_text())
 with db() as conn,conn.cursor() as q:
  q.execute("SELECT COUNT(*) AS count FROM delivery_jobs WHERE Status IN ('Pending','Processing','Deferred')");assert q.fetchone()['count']==0,'Refuse DNS isolation with any existing active delivery jobs'
 sink_tls=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER);sink_tls.load_cert_chain(WORK/'tls/server.pem',WORK/'tls/server.key');client_tls=ssl.create_default_context(cafile=str(ROOT/'certificates/server-cert.pem'))
 servers=[];stop=threading.Event();results=[];blocked=set();formal=None
 try:
  # Release wildcard port 25 before binding the owned receiver; restore even if a bind fails.
  subprocess.run(['python3',str(WORK/'remote_burst_setup.py'),'fixed'],check=True)
  for factory,address,handler in ((socketserver.ThreadingUDPServer,('127.0.0.2',53),DnsUdp),(ThreadTcp,('127.0.0.2',53),DnsTcp),(ThreadTcp,('127.0.0.2',25),Sink),(ThreadTcp,('127.0.0.2',8082),CrlHttp)):
   server=factory(address,handler);servers.append(server);threading.Thread(target=server.serve_forever,daemon=True).start()
  preflight(manifest)
  if probe_only:return
  formal=time.monotonic();deadline=formal+45*60;threading.Thread(target=monitor,args=(stop,),daemon=True).start()
  # Absolute stage ends prevent a slow stage consuming all remaining stages.
  for labels,budget in [(['incoming','outgoing'],10*60),([f'{m}-{n}' for n in range(1,4) for m in ('fixed','dynamic')],25*60),([f'repeat-{n}' for n in range(1,6)],33*60)]:
   for label in labels:
    mode='dynamic' if label.startswith(('dynamic','repeat')) else 'fixed'
    if abort.is_set():break
    if mode in blocked:results.append({'round':label,'skipped':'mode did not drain'});continue
    result=run_round(label,mode,min(deadline,formal+budget),manifest);results.append(result)
    if not result.get('drained',False) and 'skipped' not in result:blocked.add(mode)
    if label.startswith('repeat') and result.get('drained'):time.sleep(min(30,max(0,deadline-time.monotonic())))
  # Independent diagnostic hook is explicitly outside formal throughput comparison.
  if not abort.is_set() and time.monotonic()<formal+38*60 and 'dynamic' not in blocked:
   diag=WORK/'remote_burst_diagnostic.py'
   if not diag.exists():raise RuntimeError('Required independent native diagnostic hook missing')
   try:results.append(run_round('diagnostic','dynamic',formal+38*60,manifest))
   finally:
    if diag.exists():subprocess.run(['python3',str(diag),'stop'],check=False)
 finally:
  stop.set()
  for server in servers:server.shutdown();server.server_close()
  write_json(WORK/'suite.json',{'results':results,'elapsedSeconds':time.monotonic()-formal if formal is not None else None,'formalDeadlineSeconds':2700,'loadGenerator':'same host outside app cgroup','mysqlOutsideAppBudget':True})
  try:
   if not probe_only:cleanup()
  finally:subprocess.run(['python3',str(WORK/'remote_burst_setup.py'),'restore'],check=True)
if __name__=='__main__':
 parser=argparse.ArgumentParser();parser.add_argument('action',choices=['prepare','run','cleanup','relocate-inputs','prepare-probes','preflight-only','prepare-crl','normalize-probe-senders','discover-probe-bounces','finalize-preflight']);args=parser.parse_args()
 if args.action=='prepare':prepare()
 elif args.action=='cleanup':cleanup()
 elif args.action=='relocate-inputs':relocate_inputs()
 elif args.action=='prepare-probes':prepare_probes()
 elif args.action=='prepare-crl':prepare_crl()
 elif args.action=='normalize-probe-senders':normalize_probe_senders()
 elif args.action=='finalize-preflight':finalize_preflight()
 elif args.action=='discover-probe-bounces':print(json.dumps({'ownedBounces':len(discover_probe_bounces())}),flush=True)
 elif args.action=='preflight-only':execute(probe_only=True)
 else:execute()
