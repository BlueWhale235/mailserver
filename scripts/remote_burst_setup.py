import json,ssl,subprocess,sys,time,shutil,hashlib,tarfile
from pathlib import Path
from urllib.request import Request,urlopen
work=Path('/mnt/storage/mailserver-burst-20261001');root=Path('/mnt/storage/mailserver');service='mailserver-evaluation'
private=json.loads((work/'private.json').read_text());tls=ssl.create_default_context(cafile=str(root/'certificates/server-cert.pem'));token=None
def api(path,body=None,method=None):
 req=Request('https://127.0.0.1:5000/api/v1'+path,data=json.dumps(body).encode() if body is not None else None,headers={'Content-Type':'application/json',**({'Authorization':'Bearer '+token} if token else {})},method=method)
 with urlopen(req,context=tls,timeout=30) as r:
  raw=r.read();return json.loads(raw) if raw else None
def login():
 global token
 for i in range(40):
  try:token=api('/auth/token',{'userName':private['user'],'password':private['password']})['accessToken'];return
  except Exception:
   if i==39:raise
   time.sleep(.5)
def run(*args):subprocess.run(args,check=True,stdout=subprocess.DEVNULL)
mode=sys.argv[1];assert mode in ('prepare','fixed','dynamic','restore')
drop=Path('/etc/systemd/system/mailserver-evaluation.service.d/burst-20261001.conf')
if mode=='restore':run('systemctl','start',service)
login()
if mode=='prepare':
 assert not drop.exists(),'Refusing an existing experiment dropin'
 (work/'original-mailserver.json').write_bytes((root/'mailserver.json').read_bytes());(work/'original-mailserver.json').chmod(0o600)
 original={k:api(v) for k,v in [('resources','/settings/resources'),('protection','/security/connection-protection'),('accounts','/accounts'),('domains','/domains')]}
 (work/'original-settings.json').write_text(json.dumps(original));(work/'original-settings.json').chmod(0o600)
 (work/'original-mailserver').write_bytes((root/'mailserver').read_bytes());(work/'original-mailserver').chmod(0o700)
 result={'originalSha256':hashlib.sha256((root/'mailserver').read_bytes()).hexdigest(),'hostDnsSha256':hashlib.sha256(Path('/etc/resolv.conf').read_bytes()).hexdigest(),'service':subprocess.check_output(['systemctl','cat',service]).decode()}
 (work/'initial-environment.json').write_text(json.dumps(result,indent=2));print(json.dumps({'prepared':True,'originalSha256':result['originalSha256']}));sys.exit()
if mode=='restore':
 original=json.loads((work/'original-settings.json').read_text())
 api('/security/connection-protection',original['protection'],'PUT')
 for a in original['accounts']:
  if a.get('address') in ('inbox@efficiency.invalid','load@efficiency.invalid'):api('/accounts/'+a['id'],{'quotaBytes':a['quotaBytes']},'PATCH')
 for d in original['domains']:
  if d['name']=='efficiency.invalid':api('/domains/'+d['id']+'/quota',{'quotaBytes':d['quotaBytes']},'PUT')
 rs=original['resources'];values=rs.get('values')
 if values is None:values=next(x['values'] for x in rs['profiles'] if x['id']==rs['profileId'])
 api('/settings/resources',{'profileId':rs['profileId'],'values':values},'PUT')
 run('systemctl','stop',service)
 (root/'mailserver.json').write_bytes((work/'original-mailserver.json').read_bytes());(root/'mailserver.json').chmod(0o600)
 drop.unlink(missing_ok=True);run('systemctl','daemon-reload');run('systemctl','start',service);login()
 result={'health':api('/health/ready'),'restoredProtection':api('/security/connection-protection')==original['protection'],'sha256':hashlib.sha256((root/'mailserver').read_bytes()).hexdigest(),'hostDnsUnchanged':hashlib.sha256(Path('/etc/resolv.conf').read_bytes()).hexdigest()==json.loads((work/'initial-environment.json').read_text())['hostDnsSha256']}
 (work/'restoration.json').write_text(json.dumps(result,indent=2));print(json.dumps(result));sys.exit()
config=json.loads((work/'original-mailserver.json').read_text());config['AdaptiveResources']={'Enabled':True,'AutomaticEnabled':mode=='dynamic','PauseBackfill':True};config['Storage']['PreparedInboundEnabled']=False;config['EfficiencyDiagnostics']={'Enabled':False};config['SmtpConnectionReuse']={'Enabled':True,'MaximumIdleConnections':16,'IdleSeconds':30,'MaximumLifetimeSeconds':300,'MaximumMessages':50}
config['Resources']={'MaximumHeavyOperations':4};config['Smtp']['MaximumActiveMessages']=32;config['Smtp']['MaximumConnections']=128
config['Smtp']['Endpoints']=[{**ep,'Address':'127.0.0.1','Security':'StartTls'} if ep['Port']==25 else ep for ep in config['Smtp']['Endpoints']]
run('systemctl','stop',service)
if (work/'candidate.tar.gz').exists():
 unpack=work/'candidate';unpack.mkdir(exist_ok=True)
 with tarfile.open(work/'candidate.tar.gz') as t:t.extractall(unpack,filter='data')
 for f in unpack.iterdir():
  if f.name=='mailserver' or f.suffix=='.pdb':shutil.copyfile(f,root/f.name);(root/f.name).chmod(0o755 if f.name=='mailserver' else 0o644)
(root/'mailserver.json').write_text(json.dumps(config,indent=2));(root/'mailserver.json').chmod(0o600)
drop.parent.mkdir(exist_ok=True);drop.write_text('[Service]\nCPUQuota=400%\nMemoryMax=4G\nMemorySwapMax=0\nEnvironment=SSL_CERT_FILE='+str(work/'tls/ca-bundle.pem')+'\nBindReadOnlyPaths='+str(work/'controlled-resolv.conf')+':/etc/resolv.conf\n')
run('systemctl','daemon-reload');run('systemctl','start',service);login()
protection=api('/security/connection-protection')
for key in ('smtpInbound','submission'):protection[key]={'perMinute':10000,'burst':2000,'maximumConcurrent':128}
api('/security/connection-protection',protection,'PUT')
for d in api('/domains'):
 if d['name']=='efficiency.invalid':api('/domains/'+d['id']+'/quota',{'quotaBytes':512*1024**3},'PUT')
for a in api('/accounts'):
 if a.get('address') in ('inbox@efficiency.invalid','load@efficiency.invalid'):api('/accounts/'+a['id'],{'quotaBytes':256*1024**3},'PATCH')
api('/settings/resources',{'profileId':'4gb','values':{'cacheBytes':32*1024**2,'entryBytes':4*1024**2,'deliveryConcurrency':32,'smtpConnections':128,'imapConnections':128,'maximumConcurrentLoads':2,'versionPollSeconds':3,'absoluteRefreshSeconds':60,'ruleFallbackBatchSize':64}},'PUT')
result={'mode':mode,'health':api('/health/ready'),'sha256':hashlib.sha256((root/'mailserver').read_bytes()).hexdigest(),'resourceLimits':subprocess.check_output(['systemctl','show',service,'-p','CPUQuotaPerSecUSec','-p','MemoryMax','-p','MemorySwapMax']).decode()};print(json.dumps(result))
