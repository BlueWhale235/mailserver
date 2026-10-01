import json,os,signal,subprocess,sys,time
from pathlib import Path
work=Path('/mnt/storage/mailserver-burst-20261001');env=os.environ.copy();env.update(DOTNET_ROOT='/tmp/mailserver-round2/runtime',DOTNET_ROOT_X64='/tmp/mailserver-round2/runtime',DOTNET_ROLL_FORWARD='LatestMajor')
if sys.argv[1]=='start':
 pid=subprocess.check_output(['systemctl','show','-p','MainPID','--value','mailserver-evaluation']).decode().strip();processes=[]
 for name,command in [('trace',['/tmp/mailserver-round2/dotnet-trace','collect','-p',pid,'--profile','dotnet-sampled-thread-time,gc-verbose','--buffersize','16','--duration','00:00:01:40','-o',str(work/'burst-diagnostic.nettrace')]),('counters',['/tmp/mailserver-round2/dotnet-counters','collect','-p',pid,'--counters','System.Runtime','--format','json','--duration','00:00:01:40','-o',str(work/'burst-diagnostic-counters.json')])]:
  log=(work/('diagnostic-'+name+'.log')).open('w');proc=subprocess.Popen(command,env=env,stdout=log,stderr=subprocess.STDOUT,start_new_session=True);processes.append({'name':name,'pid':proc.pid});log.close()
 (work/'diagnostic-processes.json').write_text(json.dumps({'appPid':pid,'processes':processes}));time.sleep(2)
else:
 data=json.loads((work/'diagnostic-processes.json').read_text())
 if data.get('stopped'):sys.exit(0)
 data['stopped']=True;(work/'diagnostic-processes.json').write_text(json.dumps(data));deadline=time.monotonic()+20
 for p in data['processes']:
  try:
   command=Path('/proc/'+str(p['pid'])+'/cmdline').read_bytes()
   if b'/tmp/mailserver-round2/dotnet-' in command:os.kill(p['pid'],signal.SIGINT)
  except (ProcessLookupError,FileNotFoundError):pass
 while time.monotonic()<deadline:
  alive=[]
  for p in data['processes']:
   try:os.kill(p['pid'],0);alive.append(p)
   except ProcessLookupError:pass
  if not alive:break
  time.sleep(.5)
 if (work/'burst-diagnostic.nettrace').exists():subprocess.run(['/tmp/mailserver-round2/dotnet-trace','convert',str(work/'burst-diagnostic.nettrace'),'--format','Speedscope','-o',str(work/'burst-diagnostic-stacks')],env=env,check=True,stdout=subprocess.DEVNULL)
