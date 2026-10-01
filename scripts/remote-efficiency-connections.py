#!/usr/bin/env python3
"""Bounded idle-session capacity test using a separate load client."""
import argparse,asyncio,json,ssl,subprocess,time
from pathlib import Path
from urllib.request import Request,urlopen

parser=argparse.ArgumentParser()
parser.add_argument('--host',required=True)
parser.add_argument('--ssh-key',type=Path,required=True)
parser.add_argument('--credentials',type=Path,required=True)
parser.add_argument('--ca-cert',type=Path,required=True)
parser.add_argument('--output',type=Path,required=True)
args=parser.parse_args()
credentials=json.loads(args.credentials.read_text(encoding='utf-8-sig'))
tls=ssl.create_default_context(cafile=str(args.ca_cert))
token=None
def api(path,body=None,method=None):
    headers={'Content-Type':'application/json',**({'Authorization':'Bearer '+token} if token else {})}
    request=Request(f'https://{args.host}:5000/api/v1'+path,data=json.dumps(body).encode() if body is not None else None,headers=headers,method=method)
    with urlopen(request,context=tls,timeout=30) as response:return json.load(response)
def ssh(command):
    return subprocess.check_output(['ssh','-n','-i',str(args.ssh_key),'-o','BatchMode=yes','root@'+args.host,command],stdin=subprocess.DEVNULL,text=True,timeout=60)
token=api('/auth/token',{'userName':credentials['user'],'password':credentials['password']})['accessToken']

async def main():
    with args.output.open('w') as output:
        for profile,tiers in [('1gb',(8,16,32)),('32gb',(100,200,400,800,1200))]:
            api('/settings/resources',{'profileId':profile},'PUT')
            ssh('supervisorctl restart mailserver:*')
            time.sleep(5)
            pid=int(ssh('supervisorctl pid mailserver:mailserver_00').strip())
            clients=[]
            async def open_one():
                try:
                    reader,writer=await asyncio.wait_for(asyncio.open_connection(args.host,25),15)
                    greeting=await asyncio.wait_for(reader.readline(),15)
                    if greeting.startswith(b'220'):
                        clients.append((reader,writer));return 'ok'
                    writer.close();await writer.wait_closed();return greeting.decode(errors='replace').strip()
                except Exception as error:return type(error).__name__
            try:
                for tier in tiers:
                    results=[]
                    for start in range(0,tier-len(clients),64):
                        results.extend(await asyncio.gather(*(open_one() for _ in range(min(64,tier-len(clients))))))
                    await asyncio.sleep(5)
                    command=f'python3 /tmp/mailserver-efficiency-monitor.py --pid {pid} --duration 1 --output /dev/stdout'
                    # Only fixed, validated numeric values enter this remote command.
                    resources=json.loads(ssh(command))
                    healthy=api('/health/ready').get('status')=='ready'
                    record={'profile':profile,'target':tier,'held':len(clients),'errors':[x for x in results if x!='ok'],
                            'resources':resources,'healthy':healthy}
                    output.write(json.dumps(record)+'\n');output.flush();print(json.dumps(record),flush=True)
                    if len(clients)!=tier or not healthy or resources.get('VmSwapBytes',0)>128*1024**2 or resources.get('MemAvailableBytes',0)<150*1024**2:break
            finally:
                for _,writer in clients:writer.close()
                await asyncio.gather(*(writer.wait_closed() for _,writer in clients),return_exceptions=True)
asyncio.run(main())
